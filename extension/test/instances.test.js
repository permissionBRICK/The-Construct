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
// An unknown backend is REPORTED but kept VERBATIM. Rewriting it to "hyperv-local"
// (which this reader used to do) promoted every typo to destructive local Hyper-V
// access — see the end-to-end gate below.
eq("messy: unknown backend is kept as written, never promoted", messy.byName["good-vm"].backend, "martian");
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
// The version must be a JSON NUMBER: a QUOTED "1" is a foreign schema. This is the
// shared-reader contract's sharpest edge -- PowerShell used to compare the two operands
// as strings and load the file, so the same bytes selected work-vm there and agent-vm
// here. Mirrored in test/instances.test.ps1; change them together.
const quotedVer = inst.load({ path: writeRegistry('{"version":"1","instances":{"work-vm":{"backend":"hyperv-local"}}}') });
ok("quoted version: a string \"1\" is NOT version 1", !quotedVer.byName["work-vm"]);
eq("quoted version: only the default remains", quotedVer.instances.length, 1);
ok("quoted version: the default is byte-identical", inst.isDefaultInstance(inst.resolve(quotedVer, null)));
ok("quoted version: reported as a problem", quotedVer.problems.some((p) => p.includes("version")));
ok("quoted version: resolving the named instance falls back to the default",
  inst.isDefaultInstance(inst.resolve(quotedVer, "work-vm")));
// ...and the numeric spellings JSON considers the same number ARE version 1.
ok("numeric version: 1.0 is version 1",
  !!inst.load({ path: writeRegistry('{"version":1.0,"instances":{"work-vm":{"backend":"hyperv-local"}}}') }).byName["work-vm"]);
ok("boolean version: true is NOT version 1",
  !inst.load({ path: writeRegistry('{"version":true,"instances":{"work-vm":{"backend":"hyperv-local"}}}') }).byName["work-vm"]);

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
// Case-SENSITIVE: "HYPERV-REMOTE" is not the enum value, so it is reported — and kept
// exactly as written, which is what makes drivers/index.js refuse it a driver on BOTH
// sides rather than one of them treating it as the local Hyper-V backend.
eq("parity: uppercase backend rejected (case-sensitive) and kept verbatim", tv.backend, "HYPERV-REMOTE");
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

// ── KEY-CASING PARITY (mirrored in test/instances.test.ps1) ──────────────────
// JavaScript property lookup is case-SENSITIVE; the PowerShell reader's was case-
// INSENSITIVE (PSObject.Properties[$name]), so ONE registry's bytes aimed the two
// readers at DIFFERENT VMs: {"VERSION":1,"DEFAULTINSTANCE":"x","INSTANCES":{...}} was
// ignored here (agent-vm) and loaded there — with "x" as the DEFAULT instance — while a
// wrong-cased "BACKEND"/"SSHHOST" inside an entry turned a derived hyperv-local instance
// into a remote one on the PS side only. Both readers now do an ORDINAL, exact-case
// lookup for every top-level and nested schema field, so a wrong-cased key is simply
// ABSENT: never a value, and never a "must be a string" problem either.
// These string literals are the EXACT bytes test/instances.test.ps1 feeds its reader —
// change the two lists together. (Deliberately no fixture spelling the SAME key twice in
// two casings: ConvertFrom-Json itself refuses that document on PowerShell 6+, so the PS
// side degrades to "not valid JSON" + the default instance — fail-closed, not a target
// disagreement, and not fixable in a 5.1-compatible way.)
console.log("\n=== key-casing parity (mirrored in test/instances.test.ps1) ===");
const CASE_FIXTURES = {
  "upper-top": '{"VERSION":1,"DEFAULTINSTANCE":"work-vm","INSTANCES":{"work-vm":{"backend":"hyperv-local"}}}',
  "mixed-top": '{"Version":1,"DefaultInstance":"work-vm","Instances":{"work-vm":{"backend":"hyperv-local"}}}',
  "upper-nested": '{"version":1,"instances":{"work-vm":{"BACKEND":"hyperv-remote","SSHHOST":"buildbox.local",' +
    '"SSHPORT":2201,"HOSTALIAS":"boxy","KEYNAME":"custom_key","CONFIGBRANCH":"branch-x",' +
    '"SCRIPTSDIR":"C:/tools","OWNER":"someone","SERVICE":{"url":"https://x"}}}}',
  "mixed-nested": '{"version":1,"instances":{"work-vm":{"Backend":"hyperv-remote","SshHost":"buildbox.local",' +
    '"VmName":"BuildBox","SshPort":"2201"}}}',
  "upper-badtype": '{"version":1,"instances":{"work-vm":{"SSHHOST":123,"KEYNAME":42}}}',
};
// Wrong-cased TOP-LEVEL keys: no version, no instances, no defaultInstance — i.e. the
// zero-change default, silently.
for (const k of ["upper-top", "mixed-top"]) {
  const r = inst.load({ path: writeRegistry(CASE_FIXTURES[k]) });
  ok(`casing (${k}): the wrong-cased "instances" bag is not read`, !r.byName["work-vm"]);
  eq(`casing (${k}): only the default instance loads`, r.instances.length, 1);
  eq(`casing (${k}): the wrong-cased defaultInstance pointer is ignored`, r.defaultInstance, "agent-vm");
  ok(`casing (${k}): the default is byte-identical to today`, inst.isDefaultInstance(inst.resolve(r, null)));
  deepEq(`casing (${k}): silent — an absent key is not a malformed file`, r.problems, []);
}
// Wrong-cased ENTRY fields: the entry loads, but every field is DERIVED (which is also
// what makes it a canonical hyperv-local instance rather than a skipped one).
for (const k of ["upper-nested", "mixed-nested", "upper-badtype"]) {
  const r = inst.load({ path: writeRegistry(CASE_FIXTURES[k]) });
  const e = r.byName["work-vm"];
  ok(`casing (${k}): the entry itself still loads (its NAME is exact)`, !!e);
  if (!e) continue;
  eq(`casing (${k}): "BACKEND" ignored -> derived hyperv-local`, e.backend, "hyperv-local");
  eq(`casing (${k}): "SSHHOST" ignored -> derived host`, e.vmHost, "work-vm.mshome.net");
  eq(`casing (${k}): "SSHPORT" ignored -> 22`, e.sshPort, 22);
  eq(`casing (${k}): "HOSTALIAS"/"VmName" ignored -> derived`, e.hostAlias, "work-vm");
  eq(`casing (${k}): "VmName" ignored -> derived`, e.vmName, "work-vm");
  eq(`casing (${k}): "KEYNAME" ignored -> derived`, e.keyName, "construct_work-vm_ed25519");
  eq(`casing (${k}): "CONFIGBRANCH" ignored -> derived branch`, e.configBranch, "vm-work-vm");
  eq(`casing (${k}): "SCRIPTSDIR" ignored -> null`, e.scriptsDir, null);
  eq(`casing (${k}): "OWNER" ignored -> null`, e.owner, null);
  eq(`casing (${k}): "SERVICE" ignored -> null`, e.service, null);
  ok(`casing (${k}): a wrong-cased entry is a DEFAULT-behaving local instance`,
    !inst.localIdentityProblems(e).length);
  deepEq(`casing (${k}): no problems (not even a type complaint)`, r.problems, []);
}

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
// The shapes that MUST keep working. A free-form ENDPOINT belongs to a non-local
// backend (a hyperv-local instance's identity is pinned to its name — see the canonical
// identity block below), so the host/alias/key cases are stated on a remote entry.
for (const good of [
  { backend: "hyperv-remote", sshHost: "buildbox.example.local" },
  { backend: "hyperv-remote", sshHost: "10.0.0.7" }, { backend: "hyperv-remote", sshHost: "host" },
  { backend: "hyperv-remote", sshHost: "fe80::1" },
  { backend: "hyperv-remote", sshHost: "2001:db8::8a2e:370:7334" },
  { backend: "hyperv-remote", sshHost: "buildbox.local", keyName: "construct_work-vm_ed25519" },
  { backend: "hyperv-remote", sshHost: "buildbox.local", hostAlias: "work-vm.local" },
  { keyName: "construct_work-vm_ed25519" },
  { vmName: "Work-VM" }, { configBranch: "vm-work" }, { configBranch: "feature.x_1" },
  // Both readers TRIM a string field first, so surrounding whitespace is not a problem.
  { backend: "hyperv-remote", sshHost: " buildbox.local\n" },
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
  version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "good.local", vmHost: "-x; calc" } },
})) });
ok("identity: an invalid LOSING vmHost still skips the instance", !dualHost.byName["work-vm"]);
ok("identity: ...and names the field that is wrong", dualHost.problems.some((p) => p.includes('"vmHost"')));
const dualHost2 = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "-x; calc", vmHost: "good.local" } },
})) });
ok("identity: an invalid WINNING sshHost skips it too", !dualHost2.byName["work-vm"]);
ok("identity: ...reported once, not twice",
  dualHost2.problems.filter((p) => p.includes("is not a host name or IP address")).length === 1,
  JSON.stringify(dualHost2.problems));
const dualOk = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "good.local", vmHost: "other.local" } },
})) });
eq("identity: two VALID spellings still load, sshHost winning", dualOk.byName["work-vm"].vmHost, "good.local");

// The key-file rule is STRICTER than the alias rule, and only for keyName.
for (const v of ["CON", "con", "NUL", "COM1", "lpt9", "CON.txt", "con.key.txt", "agent_vm_ed25519."]) {
  ok(`keyfile: ${JSON.stringify(v)} is refused as a key file name`, !inst.isKeyFileName(v));
  ok(`keyfile: ${JSON.stringify(v)} is still a fine ssh alias`, inst.isSafeToken(v));
  const r = inst.load({ path: writeRegistry(JSON.stringify({
    version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.local", hostAlias: v } },
  })) });
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

// ── END-TO-END: registry TEXT -> resolve -> lifecycle.buildInvocation ────────
// The two rules below are only worth anything if they hold along the WHOLE path a real
// action takes, so these cases start from raw JSON (parseRegistry) and end at the
// launched invocation — not at a hand-built instance object, which is how the earlier
// backend-gating tests missed the coercion bug in the first place.
console.log("\n=== unknown backends are never promoted (registry -> lifecycle) ===");
const parse = (doc) => inst.parseRegistry(JSON.stringify(doc));
const E2E_PARAMS = ["VmHost", "HostAlias", "SshPort", "LocalKeyName", "VmName", "ConfigBranch"];
const invoke = (action, instance) => life.buildInvocation(action, {
  settings: { gitName: "Neo" }, backupDir: "C:\\b", enabled: true,
  instance, instanceParams: E2E_PARAMS,
});
// An entry that CANNOT launch a targeted action, whatever else it says: `resolve` hands
// back the default instance, so the invocation carries no target identity at all.
const cannotTarget = (label, parsed) => {
  const resolved = inst.resolve(parsed.registry, "work-vm");
  ok(`${label}: resolve falls back to the default instance`, inst.isDefaultInstance(resolved));
  for (const action of ["reinstall", "redownload", "setCheckpoints"]) {
    const r = invoke(action, resolved);
    deepEq(`${label}: ${action} carries no target args`,
      r.args.filter((a) => ["-VmName", "-VmHost", "-HostAlias", "-SshPort", "-LocalKeyName"].includes(a)), []);
  }
};
// A GENUINELY unknown backend is kept verbatim: the driver dispatch degrades on it
// correctly (unknownDriver), which is what refuses the destructive actions.
for (const backend of ["proxmox", "hyperv-remtoe", "HYPERV-REMOTE"]) {
  const parsed = parse({ version: 1, instances: { "work-vm": { backend, sshHost: "buildbox.local" } } });
  const entry = parsed.registry.byName["work-vm"];
  ok(`backend(${backend}): the entry survives`, !!entry);
  eq(`backend(${backend}): it is NOT rewritten to hyperv-local`, entry.backend, backend);
  ok(`backend(${backend}): drivers refuse it a hypervisor driver`,
    drivers.getDriver(entry.backend).unknown === true &&
    drivers.getDriver(entry.backend).capabilities.hostLifecycle === false);
  for (const action of ["reinstall", "redownload", "setCheckpoints"]) {
    const r = invoke(action, inst.resolve(parsed.registry, "work-vm"));
    ok(`backend(${backend}): ${action} is BLOCKED`, r.blocked === true);
    ok(`backend(${backend}): ${action} launches nothing`, r.script === undefined && r.args === undefined);
  }
  for (const action of ["reprovision", "exportConfig"]) {
    const r = invoke(action, inst.resolve(parsed.registry, "work-vm"));
    ok(`backend(${backend}): ${action} stays allowed (pure SSH)`, !r.blocked && !!r.script);
    ok(`backend(${backend}): ${action} targets the entry's own endpoint`, r.args.includes("buildbox.local"));
  }
  ok(`backend(${backend}): reported as unknown`, parsed.problems.some((p) => p.includes(backend)));
}

// A PRESENT BUT UNUSABLE backend is NOT "no backend": deriving hyperv-local from it would
// hand destructive local Hyper-V access to a value the file never actually stated. Only
// an ABSENT (or JSON-null) backend derives the default, so these entries never load — and
// therefore can never reach a rebuild. Every case is driven from raw JSON to the
// invocation, including the otherwise-canonical ones (nothing but the backend is wrong).
for (const [label, raw] of [
  ["42", 42], ["true", true], ["empty string", ""], ["whitespace", "   "],
  ["an array", ["hyperv-local"]], ["an object", { id: "hyperv-local" }],
]) {
  for (const [shape, extra] of [["canonical", {}], ["with a foreign host", { sshHost: "buildbox.local" }]]) {
    const parsed = parse({ version: 1, instances: { "work-vm": { backend: raw, ...extra } } });
    ok(`backend(${label}, ${shape}): the entry is SKIPPED`, !parsed.registry.byName["work-vm"]);
    ok(`backend(${label}, ${shape}): the problem names "backend" and says skipped`,
      parsed.problems.some((p) => p.includes('"backend"') && p.includes("skipped")));
    ok(`backend(${label}, ${shape}): not reported as a plain type problem`,
      !parsed.problems.some((p) => p.includes('"backend" must be a string')));
    cannotTarget(`backend(${label}, ${shape})`, parsed);
  }
}
// A JSON null (like an absent key) IS "omitted" — that stays the zero-change default.
const nullBackend = parse({ version: 1, instances: { "work-vm": { backend: null } } });
eq("backend(null): omitted-equivalent, derives hyperv-local",
  nullBackend.registry.byName["work-vm"].backend, "hyperv-local");
deepEq("backend(null): and reports nothing", nullBackend.problems, []);

// A SPELLING THE TWO LOOKUPS READ DIFFERENTLY. Every enum comparison in both readers is
// case-sensitive ("unknown"), but drivers.getDriver trims + lowercases before the lookup
// and WOULD return the local driver (hostLifecycle: true) — so an otherwise CANONICAL
// entry would drive destructive local actions. Neither reading is safe, so it is skipped.
for (const backend of ["HYPERV-LOCAL", "Hyperv-Local", "  hyperv-LOCAL  "]) {
  ok(`backend(${JSON.stringify(backend)}): getDriver WOULD hand it the local driver`,
    drivers.getDriver(backend).capabilities.hostLifecycle === true);
  const parsed = parse({ version: 1, instances: { "work-vm": { backend } } });   // canonical otherwise
  ok(`backend(${JSON.stringify(backend)}): the entry is SKIPPED`, !parsed.registry.byName["work-vm"]);
  ok(`backend(${JSON.stringify(backend)}): the problem names "backend"`,
    parsed.problems.some((p) => p.includes('"backend"') && p.includes("skipped")));
  cannotTarget(`backend(${JSON.stringify(backend)})`, parsed);
}
// ...while the EXACT value (and one that only needed trimming, which both readers do to
// every string field) is the ordinary local backend.
for (const backend of ["hyperv-local", " hyperv-local "]) {
  const parsed = parse({ version: 1, instances: { "work-vm": { backend } } });
  ok(`backend(${JSON.stringify(backend)}): loads as the local backend`,
    parsed.registry.byName["work-vm"] && parsed.registry.byName["work-vm"].backend === "hyperv-local");
  deepEq(`backend(${JSON.stringify(backend)}): with no problems`, parsed.problems, []);
}

// ── The CANONICAL identity of a hyperv-local instance ───────────────────────
// reinstall/redownload emit ONLY -VmName; Auto-Install.ps1 derives the guest host, the
// alias and the key from it. So a local entry that states anything else would rebuild a
// DIFFERENT VM than it dials — and be unable to reach the one it rebuilt.
console.log("\n=== canonical local identity (registry -> lifecycle) ===");
const nonCanonical = [
  // The headline case: "work-vm" pointed at the DEFAULT VM. A reinstall would delete and
  // recreate Agent-VM under the guise of rebuilding work-vm.
  ["vmName of another VM", { vmName: "Agent-VM" }, "vmName"],
  ["a foreign sshHost", { sshHost: "buildbox.local" }, "sshHost"],
  // The legacy alias convention: the registry's alias is the BARE instance name.
  ["the legacy construct- alias", { hostAlias: "construct-work-vm" }, "hostAlias"],
  ["a custom key file", { keyName: "custom_key" }, "keyName"],
  ["a non-standard port", { sshPort: 2201 }, "sshPort"],
];
for (const [label, entry, field] of nonCanonical) {
  const parsed = parse({ version: 1, instances: { "work-vm": { backend: "hyperv-local", ...entry } } });
  ok(`canonical(${label}): the instance is SKIPPED`, !parsed.registry.byName["work-vm"]);
  ok(`canonical(${label}): the problem names "${field}"`,
    parsed.problems.some((p) => p.includes(`"${field}"`) && p.includes("skipped")));
  // ...so it can never reach a launched command: resolve falls back to the DEFAULT
  // instance, which targets nothing and emits no -VmName of its own.
  const resolved = inst.resolve(parsed.registry, "work-vm");
  ok(`canonical(${label}): resolve falls back to the default instance`, inst.isDefaultInstance(resolved));
  deepEq(`canonical(${label}): a rebuild carries no target args at all`,
    invoke("reinstall", resolved).args.filter((a) => a === "-VmName"), []);
}
// An omitted backend is the same rule (it derives hyperv-local).
ok("canonical: a backendless entry is held to the same rule",
  !parse({ version: 1, instances: { "work-vm": { vmName: "Agent-VM" } } }).registry.byName["work-vm"]);
// The DEFAULT instance's own entry is canonical with today's literals, and a deviating
// one degrades to the synthesized default rather than to half an identity.
ok("canonical: the default instance's literals are canonical",
  !!parse({ version: 1, instances: { "agent-vm": {
    backend: "hyperv-local", vmName: "Agent-VM", sshHost: "agent-vm.mshome.net", sshPort: 22,
    hostAlias: "agent-vm", keyName: "agent_vm_ed25519", configBranch: "vm",
  } } }).registry.byName["agent-vm"]);
ok("canonical: a deviating agent-vm entry degrades to the synthesized default",
  inst.isDefaultInstance(parse({ version: 1, instances: { "agent-vm": { vmName: "Other-VM" } } })
    .registry.byName["agent-vm"]));
// Hyper-V VM names are case-insensitive, so only the LOWERCASED name must match.
ok("canonical: a differently-cased vmName is fine",
  !!parse({ version: 1, instances: { "work-vm": { vmName: "Work-VM" } } }).registry.byName["work-vm"]);
// The -ConfigBranch override must keep working: it is the one field the launched scripts
// can be TOLD, so an explicit branch is not a deviation.
const branchOverride = parse({ version: 1, instances: { "work-vm": { configBranch: "vm-team" } } });
ok("canonical: an explicit configBranch is still allowed", !!branchOverride.registry.byName["work-vm"]);
eq("canonical: ...and is threaded to the rebuild as -ConfigBranch",
  (() => { const a = invoke("reinstall", inst.resolve(branchOverride.registry, "work-vm")).args;
    return a[a.indexOf("-ConfigBranch") + 1]; })(), "vm-team");
// A canonical entry is the positive control — it rebuilds and reprovisions ITSELF.
const canonical = parse({ version: 1, instances: { "work-vm": { backend: "hyperv-local" } } });
const canonInst = inst.resolve(canonical.registry, "work-vm");
deepEq("canonical: a canonical entry loads with no problems", canonical.problems, []);
eq("canonical: it is the entry, not the default", canonInst.name, "work-vm");
const canonPair = (args, flag) => { const i = args.indexOf(flag); return i < 0 ? null : args[i + 1]; };
eq("canonical: reinstall targets -VmName work-vm", canonPair(invoke("reinstall", canonInst).args, "-VmName"), "work-vm");
eq("canonical: redownload targets -VmName work-vm", canonPair(invoke("redownload", canonInst).args, "-VmName"), "work-vm");
const canonRepro = invoke("reprovision", canonInst).args;
eq("canonical: reprovision dials the derived host", canonPair(canonRepro, "-VmHost"), "work-vm.mshome.net");
eq("canonical: reprovision uses the derived alias", canonPair(canonRepro, "-HostAlias"), "work-vm");
eq("canonical: reprovision uses the derived key", canonPair(canonRepro, "-LocalKeyName"), "construct_work-vm_ed25519");
eq("canonical: reprovision uses port 22", canonPair(canonRepro, "-SshPort"), "22");

// ── Cross-entry identity collisions ─────────────────────────────────────────
// Two names for one machine: a rebuild of one would delete the other's VM, and a
// reprovision would overwrite its key file.
console.log("\n=== identity collisions ===");
for (const [label, entry, field] of [
  ["the default VM's name", { vmName: "agent-vm" }, "vmName"],
  ["the default VM's host", { sshHost: "agent-vm.mshome.net" }, "sshHost"],
  ["the default VM's alias", { hostAlias: "agent-vm" }, "hostAlias"],
  ["the default VM's key", { keyName: "agent_vm_ed25519" }, "keyName"],
]) {
  const parsed = parse({ version: 1, instances: {
    "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.local", ...entry },
  } });
  ok(`collision(${label}): the entry is skipped`, !parsed.registry.byName["work-vm"]);
  ok(`collision(${label}): the problem names ${field}`,
    parsed.problems.some((p) => p.includes(field) && p.includes("skipped")));
  ok(`collision(${label}): the default instance survives untouched`,
    inst.isDefaultInstance(parsed.registry.byName["agent-vm"]));
}
const shared = parse({ version: 1, instances: {
  "a-vm": { backend: "hyperv-remote", sshHost: "buildbox.local" },
  "b-vm": { backend: "hyperv-remote", sshHost: "BuildBox.local" },
} });
ok("collision: two entries sharing a host drop BOTH (nothing says which is the impostor)",
  !shared.registry.byName["a-vm"] && !shared.registry.byName["b-vm"]);
ok("collision: ...and it is reported once, naming both",
  shared.problems.filter((p) => p.includes("share the same")).length === 1 &&
  shared.problems.some((p) => p.includes("a-vm") && p.includes("b-vm")));
ok("collision: the comparison is case-insensitive (one NTFS file, one DNS name)",
  !parse({ version: 1, instances: {
    "a-vm": { backend: "hyperv-remote", sshHost: "one.local", keyName: "Shared_Key" },
    "b-vm": { backend: "hyperv-remote", sshHost: "two.local", keyName: "shared_key" },
  } }).registry.byName["a-vm"]);
deepEq("collision: two canonical local instances never collide",
  parse({ version: 1, instances: { "a-vm": {}, "b-vm": {}, "agent-vm": {} } }).problems, []);

// ── configBranch is a cross-entry identity ───────────────────────────────────
// The branch IS the instance's store inside the ONE host config repo (docs/config-sync.md,
// "Multiple instances"): two entries on one branch share their VM snapshots, deletion
// history, merge base and write-backs, so one VM's tick merges -- or deletes -- the
// other VM's configuration. Mirrored in test/instances.test.ps1.
console.log("\n=== configBranch uniqueness (one branch per VM) ===");
const branchClaim = parse({ version: 1, instances: {
  "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.local", configBranch: "vm" },
} });
ok("branch: a non-default entry may NOT claim the default instance's \"vm\"",
  !branchClaim.registry.byName["work-vm"]);
ok("branch: ...and the problem names configBranch and the default instance",
  branchClaim.problems.some((p) => p.includes("configBranch") && p.includes("agent-vm") && p.includes("skipped")));
ok("branch: the default instance itself survives that entry untouched",
  inst.isDefaultInstance(branchClaim.registry.byName["agent-vm"]));
const sharedBranch = parse({ version: 1, instances: {
  "a-vm": { backend: "hyperv-remote", sshHost: "one.local", configBranch: "vm-team" },
  "b-vm": { backend: "hyperv-remote", sshHost: "two.local", configBranch: "VM-Team" },
} });
ok("branch: two entries sharing one branch drop BOTH (case-insensitively -- Windows loose refs)",
  !sharedBranch.registry.byName["a-vm"] && !sharedBranch.registry.byName["b-vm"]);
ok("branch: ...reported once, naming both and the field",
  sharedBranch.problems.filter((p) => p.includes("share the same configBranch")).length === 1 &&
  sharedBranch.problems.some((p) => p.includes("a-vm") && p.includes("b-vm")));
// The derived branches (vm-<name>) are unique by construction, so nothing changes for
// an ordinary multi-instance registry -- including the default instance's own "vm".
deepEq("branch: derived branches never collide",
  parse({ version: 1, instances: { "a-vm": {}, "b-vm": {}, "agent-vm": {} } }).problems, []);
ok("branch: a distinct explicit override is still allowed",
  !!parse({ version: 1, instances: { "work-vm": { configBranch: "vm-team" } } }).registry.byName["work-vm"]);
// The WRITE side refuses what the reader would skip -- an entry that vanished on the
// next load would leave the user with a picker that silently lost an instance.
const mutBase = parse({ version: 1, instances: { "a-vm": { configBranch: "vm-team" } } }).registry;
let threw = null;
try { inst.addInstance(mutBase, "b-vm", { configBranch: "vm-team" }); } catch (e) { threw = e; }
ok("branch: addInstance refuses a branch another instance already owns",
  !!threw && /configBranch/.test(threw.message) && /share the same/.test(threw.message));
threw = null;
try { inst.addInstance(mutBase, "b-vm", { configBranch: "vm" }); } catch (e) { threw = e; }
ok("branch: addInstance refuses the default instance's reserved \"vm\"",
  !!threw && /configBranch/.test(threw.message) && /agent-vm/.test(threw.message));
threw = null;
try { inst.addInstance(mutBase, "b-vm", { configBranch: "VM" }); } catch (e) { threw = e; }
ok("branch: ...in any spelling (the branch validator refuses a case variant of vm)",
  !!threw && /configBranch/.test(threw.message));
threw = null;
try { inst.updateInstance(inst.addInstance(mutBase, "b-vm", {}), "b-vm", { configBranch: "vm-team" }); }
catch (e) { threw = e; }
ok("branch: updateInstance refuses moving an instance onto another's branch",
  !!threw && /configBranch/.test(threw.message));
ok("branch: a distinct branch is accepted by the mutators",
  !!inst.addInstance(mutBase, "b-vm", { configBranch: "vm-other" }).byName["b-vm"]);

// ── Object.prototype names are NOT registry entries ──────────────────────────
// A name->instance map keyed by file data must not inherit Object.prototype: with a
// plain {}, byName["constructor"] is truthy for EVERY registry, so a file that merely
// POINTS at such a name resolved to Object's constructor FUNCTION and handed it out as
// an instance (undefined vmHost/keyName in every ssh argv) -- while the PowerShell
// reader's ordinal Hashtable correctly reported "no entry" and used agent-vm. Same
// fixtures in test/instances.test.ps1 -- change both together.
console.log("\n=== Object.prototype name parity ===");
const PROTO_NAMES = ["constructor", "__proto__", "toString", "hasOwnProperty", "valueOf", "isPrototypeOf"];
for (const name of PROTO_NAMES) {
  // (a) as defaultInstance, with NO entry of that name.
  const asDefault = parse({ version: 1, defaultInstance: name, instances: {} });
  eq(`proto(${name}): defaultInstance falls back to agent-vm`, asDefault.registry.defaultInstance, "agent-vm");
  ok(`proto(${name}): ...and it is reported as having no entry`,
    asDefault.problems.some((p) => p.includes("has no entry") || p.includes("not a valid instance name")));
  const active = inst.resolveActive({ registry: asDefault.registry });
  ok(`proto(${name}): the active instance is the synthesized default`,
    inst.isDefaultInstance(active.instance) && active.instance.name === "agent-vm");
  // (b) as the construct.instance SETTING (a global pin nothing validates as a name).
  const pinned = inst.resolveActive({ registry: asDefault.registry, setting: name });
  ok(`proto(${name}): a construct.instance pin of it is "not in the registry"`,
    pinned.source === "default" && inst.isDefaultInstance(pinned.instance) &&
    !!pinned.problem && pinned.problem.includes("not in the registry"));
  // (c) as the window's persisted workspace selection.
  const ws = inst.resolveActive({ registry: asDefault.registry, workspaceValue: name });
  ok(`proto(${name}): a stale workspace selection of it is skipped too`,
    ws.source === "default" && inst.isDefaultInstance(ws.instance));
  // (d) resolve()/hasInstance() agree: it is not a member.
  ok(`proto(${name}): resolve() falls back to the default instance`,
    inst.isDefaultInstance(inst.resolve(asDefault.registry, name)));
  ok(`proto(${name}): hasInstance() is false`, !inst.hasInstance(asDefault.registry, name));
  // (e) as an INSTANCE NAME in the file: the name rule decides, and it is the same
  //     decision in both readers -- only lowercase slugs are names at all.
  const asEntry = parse({ version: 1, instances: { [name]: { backend: "hyperv-remote", sshHost: "buildbox.local" } } });
  if (inst.isValidName(name)) {
    // "constructor" is a perfectly good instance name -- it must load like any other.
    ok(`proto(${name}): a real instance of that name LOADS`, !!asEntry.registry.byName[name]);
    eq(`proto(${name}): ...and resolves to itself, not to a prototype member`,
      inst.resolve(asEntry.registry, name).vmHost, "buildbox.local");
    ok(`proto(${name}): ...and hasInstance() sees it`, inst.hasInstance(asEntry.registry, name));
    ok(`proto(${name}): ...and it can be the defaultInstance`,
      parse({ version: 1, defaultInstance: name, instances: { [name]: { backend: "hyperv-remote", sshHost: "buildbox.local" } } })
        .registry.defaultInstance === name);
  } else {
    ok(`proto(${name}): an invalid name is skipped with a problem`,
      !asEntry.registry.byName[name] &&
      asEntry.problems.some((p) => p.includes("is invalid") && p.includes("skipped")));
  }
}
// The maps themselves carry no prototype, so nothing downstream can inherit one either.
const protoReg = parse({ version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.local" } } }).registry;
ok("proto: byName has a null prototype", Object.getPrototypeOf(protoReg.byName) === null);
ok("proto: a loaded registry's byName has a null prototype too",
  Object.getPrototypeOf(inst.load({ path: path.join(tmpRoot, "nope", "instances.json") }).byName) === null);
ok("proto: a mutated registry keeps it",
  Object.getPrototypeOf(inst.addInstance(protoReg, "b-vm", {}).byName) === null);
ok("proto: the file document's instances map keeps it",
  Object.getPrototypeOf(inst.toFileDocument(protoReg).instances) === null);
// ...and a hostile "__proto__" entry can never pollute one (JSON.parse makes it an own
// property, the name rule then rejects it).
const polluted = inst.parseRegistry('{"version":1,"instances":{"__proto__":{"vmName":"pwned"}}}');
ok("proto: a __proto__ entry is skipped and pollutes nothing",
  !polluted.registry.byName["__proto__"] && ({}).vmName === undefined &&
  inst.isDefaultInstance(polluted.registry.byName["agent-vm"]));

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
  // A non-default PORT belongs to a non-local backend (a hyperv-local instance is always
  // reached on 22), so the matrix runs on a remote entry with a stated endpoint.
  try { r = inst.load({ path: writeRegistry('{"version":1,"instances":{"p-vm":{"backend":"hyperv-remote","sshHost":"p-vm.example.local","sshPort":' + literal + '}}}') }); }
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
const added = inst.addInstance(base, "work-vm", { backend: "hyperv-remote", sshHost: "buildbox.local", sshPort: 2201 });
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
// The WRITE side applies the reader's own rules, so a mutator can never persist an entry
// the next load would drop (it would just vanish from the picker).
ok("add: refuses a hyperv-local instance with a foreign identity",
  (() => { try { inst.addInstance(base, "work-vm", { vmName: "Agent-VM" }); return false; } catch (_) { return true; } })());
ok("add: refuses a hostile field", (() => {
  try { inst.addInstance(base, "work-vm", { backend: "hyperv-remote", sshHost: "-x; calc" }); return false; } catch (_) { return true; }
})());
ok("add: refuses an entry that claims the default instance's key",
  (() => {
    try { inst.addInstance(base, "work-vm", { backend: "hyperv-remote", sshHost: "buildbox.local", keyName: "agent_vm_ed25519" }); return false; }
    catch (_) { return true; }
  })());
ok("add: a canonical local instance is accepted",
  !!inst.addInstance(base, "work-vm", {}).byName["work-vm"]);
const updated = inst.updateInstance(added, "work-vm", { sshPort: 2299 });
eq("update: field changed", updated.byName["work-vm"].sshPort, 2299);
eq("update: untouched field preserved", updated.byName["work-vm"].vmHost, "buildbox.local");
eq("update: backend preserved", updated.byName["work-vm"].backend, "hyperv-remote");
ok("update: rejects an unknown name", (() => { try { inst.updateInstance(added, "ghost", {}); return false; } catch (_) { return true; } })());
ok("update: rejects a change that would collide with the default instance",
  (() => { try { inst.updateInstance(added, "work-vm", { sshHost: "agent-vm.mshome.net" }); return false; } catch (_) { return true; } })());
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
// The gate asks whether the script DECLARES -ConfigBranch, not whether this instance
// has a value to emit. workInst's branch is the CANONICAL "vm-work-vm", so nothing is
// emitted -- but a script that predates the parameter has no per-alias derivation
// either: it would initialise and sync refs/heads/vm while the panel uses
// refs/heads/vm-work-vm. (The Phase-1 scripts at f84f554 are exactly that shape:
// every identity parameter, no -ConfigBranch.)
for (const action of ["reprovision", "reinstall", "redownload"]) {
  const canon = life.buildInvocation(action, {
    settings: SETTINGS, instance: workInst, instanceParams: ALL.filter((p) => p !== "ConfigBranch"),
  });
  ok(`branch: ${action} fails closed for a CANONICAL branch too`, canon.blocked === true);
  ok(`branch: ${action} the canonical refusal names the branch`, /vm-work-vm/.test(canon.reason || ""));
  ok(`branch: ${action} the canonical refusal says to update the scripts`,
    /Update the Construct scripts/.test(canon.reason || ""));
}
// ...and none of that touches the zero-change default path: the default instance is
// never gated, however old the installed scripts are.
for (const action of ["reprovision", "reinstall", "redownload"]) {
  ok(`branch: the DEFAULT instance is never blocked by the gate (${action})`,
    !life.buildInvocation(action, { settings: SETTINGS, instance: d, instanceParams: [] }).blocked);
  ok(`branch: the DEFAULT instance still emits no -ConfigBranch (${action})`,
    life.buildInvocation(action, { settings: SETTINGS, instance: d, instanceParams: ALL })
      .args.indexOf("-ConfigBranch") < 0);
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

// ── extension.js wiring: per-instance import coalescing + merge branch ───────
// These live in extension.js, which can't be required under plain node (it needs
// `vscode`), so they are pinned at the source level -- the same way vmpower.test.js
// pins the checkpoint marker. What they guard: an import of instance A must never be
// joined (or throttled away) by a caller asking about B, and a completed merge must be
// committed with the ACTIVE instance's branch in its message.
console.log("\n=== extension.js wiring (source-pinned) ===");
const extSrc = fs.readFileSync(path.join(__dirname, "..", "extension.js"), "utf8");
ok("import: the scan is coalesced through the per-instance coalescer",
  extSrc.includes("instances.createCoalescer({ throttleMs: SYNC_TICK_MIN_MS })") &&
  extSrc.includes("importCoalescer.run(t.name, force, function () { return importFromVm(t); })") &&
  !extSrc.includes("importInflightPromise"));
ok("import: the target is captured before the scan and used for the SSH cfg",
  extSrc.includes("const scanTarget = importTargetOf(target)") &&
  extSrc.includes("cfg: scanTarget.cfg") &&
  extSrc.includes("importCoalescer.stamp(scanTarget.name)"));
const preFlightFn = extSrc.slice(extSrc.indexOf("async function lifecyclePreFlight"),
  extSrc.indexOf("function showPreFlightBlock"));
ok("import: the lifecycle pre-flight scans ITS captured target, not the active one",
  preFlightFn.includes("coalescedImport(true, target)") && !preFlightFn.includes("coalescedImport(true)"));
ok("import: the sync tick's post-tick scan uses the tick's own instance",
  extSrc.includes("var syncTarget = captureTargetFull(target);") &&
  extSrc.includes("var syncInstance = targetInstance(syncTarget);") &&
  extSrc.includes("coalescedImport(true, syncTarget)"));
ok("import: the scan keeps the WHOLE capture and hands it to the profile auto-enable",
  extSrc.includes("function importTargetOf(target) {\n  return captureTargetFull(target);\n}") &&
  extSrc.includes("await autoEnableNewProfiles(profilesBefore, host.listProjectProfiles(projRoot), scanTarget)") &&
  !extSrc.includes("return { name: target.name, cfg: target.cfg };"));
ok("merge: a completed pending merge is committed on the CAPTURED instance's branch",
  extSrc.includes("var gateTarget = captureTargetFull(target);") &&
  extSrc.includes("completePendingMerge(runGit, dir, gateTarget.instance.configBranch)") &&
  !extSrc.includes("completePendingMerge(runGit, dir, activeInstance().configBranch)"));
// ...and the branch WRITE is gated before it happens and after the repo reads, with a
// stale gate reported as blocked (never {blocked:false}) so a destructive pre-flight
// fails closed rather than proceeding on an indeterminate answer.
ok("merge: the gate aborts around the write and fails CLOSED when it goes stale",
  extSrc.split('targetStale(gateTarget, "The config merge gate")').length === 3 &&
  extSrc.includes("return { blocked: true, stale: true, dir: dir, reason: STALE_GATE_REASON };") &&
  extSrc.includes("if (gate.stale) {") &&
  extSrc.includes("const STALE_GATE_REASON ="));
ok("import: the scan and the import both abort on a stale generation, before any write",
  extSrc.includes('if (targetStale(scanTarget, "The VM repo scan")) return null;') &&
  extSrc.includes('if (targetStale(scanTarget, "The VM repo import")) return null;') &&
  // ...and both sit BEFORE the profile writes and the throttle stamp.
  extSrc.indexOf('targetStale(scanTarget, "The VM repo import")') <
    extSrc.indexOf("host.writeProjectProfileIfAbsent(projRoot, item.name, item.profile)") &&
  extSrc.indexOf('targetStale(scanTarget, "The VM repo import")') <
    extSrc.indexOf("importCoalescer.stamp(scanTarget.name)"));
ok("sync: BOTH post-tick follow-ups (auto-enable and import) stop on a stale generation",
  extSrc.includes('var followUpsStale = targetStale(syncTarget, "The post-tick follow-ups");') &&
  extSrc.includes("if (!followUpsStale && result && result.ok && !result.lockBusy) {") &&
  extSrc.includes("if (!followUpsStale && result && result.ok && result.vmReadOk) {"));

// The three flows the integration review found re-reading "the active instance" after
// an await. Each one's binding is pinned here; the ORDERING rules they rely on are
// driven with deferred promises in asyncTests() below (createTargetQueue /
// planCapturedFollowUp), which is where the discard logic actually lives.
ok("sync: runConfigSync takes a captured target and uses it for the branch, cfg and scripts dir",
  extSrc.includes("async function runConfigSync(target) {") &&
  extSrc.includes("var syncTarget = captureTargetFull(target);") &&
  extSrc.includes("vmBranch: syncInstance.configBranch") &&
  extSrc.includes("cfg: syncCfg") &&
  extSrc.includes("autoEnableNewProfiles(profilesBeforeTick, host.listProjectProfiles(dir), syncTarget)"));
ok("sync: the queued follow-up is keyed by target and re-runs for THAT target",
  extSrc.includes("const syncTickFollowups = instances.createTargetQueue()") &&
  extSrc.includes("syncTickFollowups.queue(syncTarget.name, syncTickPromise, function () {") &&
  extSrc.includes("return runConfigSync(syncTarget);"));
ok("sync: every caller passes its own captured target (no bare runConfigSync())",
  !/runConfigSync\(\)/.test(extSrc) &&
  extSrc.includes("runConfigSync(syncNowTarget)") && extSrc.includes("runConfigSync(target)") &&
  extSrc.includes("runConfigSync(delTarget)") && extSrc.includes("runConfigSync(actionTarget())"));
ok("sync: profile auto-enable writes into the TARGET's CAPTURED scripts dir",
  extSrc.includes("async function autoEnableNewProfiles(before, after, target) {") &&
  extSrc.includes("var enableTarget = captureTargetFull(target);") &&
  extSrc.includes("var scriptsDir = enableTarget.scriptsDir;") &&
  !extSrc.includes("var scriptsDir = resolveScriptsDirFor(targetInstance(enableTarget));"));
// ...and every captured config-sync flow ABORTS on a stale generation rather than
// running against either instance -- at the tick's entry, when a queued follow-up
// finally starts, and immediately before the selection file is written.
ok("sync: a stale tick, a stale queued follow-up and a stale write all abort",
  extSrc.includes('if (targetStale(syncTarget, "The config-sync tick")) return null;') &&
  extSrc.includes('if (targetStale(syncTarget, "The queued config-sync tick")) return null;') &&
  extSrc.includes('if (targetStale(enableTarget, "The project-profile auto-enable")) return;') &&
  extSrc.split('targetStale(enableTarget, "The project-profile auto-enable")').length === 3 &&
  extSrc.includes("function targetStale(target, what) {") &&
  extSrc.includes("if (!instances.targetSuperseded(instanceGate, target)) return false;"));
// ...and the tick's TIMESTAMP and RESULT are that instance's too. Held window-globally,
// they made the panel report A's status under B's name and let A's stamp suppress B's
// first automatic tick for five minutes. The model of the rule runs in asyncTests().
ok("sync: the tick's timestamp and result are recorded PER INSTANCE",
  extSrc.includes("const syncStatus = instances.createSyncStatusStore({ throttleMs: SYNC_TICK_MIN_MS })") &&
  extSrc.includes("syncStatus.record(syncTarget.name, result);") &&
  !extSrc.includes("lastSyncTickAt") && !extSrc.includes("lastSyncResult"));
ok("sync: the panel reports the CAPTURED target's own status, not the last tick's",
  extSrc.includes("...syncStatus.status(csTarget.name),") &&
  extSrc.includes("instances.describeSyncStatus(syncStatus.lastAt(csTarget.name), recovered)"));
ok("sync: the automatic throttle is measured against THAT instance's last tick",
  extSrc.includes("const autoTarget = captureTargetFull(target);") &&
  extSrc.includes("if (!syncStatus.dueForAuto(autoTarget.name)) return;") &&
  extSrc.includes("await runConfigSync(autoTarget);"));
// The other half of the rule: what is genuinely repository-wide STAYS window-global —
// there is one config repo and one cross-process lock, so a tick for B must still wait
// behind a tick for A rather than race it.
ok("sync: the in-flight tick stays window-global (one repo, one lock)",
  extSrc.includes("let syncTickInFlight = false;") &&
  extSrc.includes("let syncTickPromise = null;") &&
  extSrc.includes("if (syncTickPromise) {") &&
  extSrc.includes("syncTickInFlight = true;"));
ok("sync: the capture carries the instance, cfg, scripts dir AND the generation token",
  extSrc.includes("function captureTargetFull(target) {") &&
  extSrc.includes("token: t.token || instanceGate.token(),") &&
  extSrc.includes("scriptsDir: t.scriptsDir !== undefined ? t.scriptsDir : resolveScriptsDirFor(instance),"));
ok("reprovision prompt: the target is captured with the scripts dir, before the toast",
  extSrc.includes("const saveTarget = actionTarget();") &&
  extSrc.includes("const scriptsDir = resolveScriptsDirFor(saveTarget.instance);") &&
  extSrc.includes("offerReprovisionForPatchSettings(scriptsDir, patchChanges, saveTarget)") &&
  extSrc.includes("function offerReprovisionForPatchSettings(scriptsDir, features, target) {"));
ok("reprovision prompt: a stale answer aborts through targetSuperseded instead of rebuilding",
  extSrc.includes("const plan = instances.planCapturedFollowUp(instanceGate, t, pick === \"Reprovision now\");") &&
  extSrc.includes("if (plan.reason === \"superseded\") { targetSuperseded(t, \"Reprovision\"); return; }") &&
  extSrc.includes("void startConstructReprovision(scriptsDir, plan.target);"));
ok("mic auto-arm: instance, cfg, scripts dir and generation are captured before the preference",
  extSrc.includes("async function maybeAutoEnableAudio(context, target) {") &&
  extSrc.includes("const scriptsDir = resolveScriptsDirFor(t.instance);") &&
  extSrc.includes("const reachable = await ssh.isReachable({ timeoutMs: 6000, cfg: t.cfg });") &&
  extSrc.includes("const plan = instances.planCapturedFollowUp(instanceGate, t, reachable);"));
ok("mic auto-arm: the probe result is discarded (not applied to the new VM) after a switch",
  extSrc.includes("enableAudio(context, undefined, { auto: true, target: plan.target })") &&
  extSrc.includes("if (opts.target && instances.targetSuperseded(instanceGate, t)) {") &&
  extSrc.includes("hostAudioInstance = t.name;") && extSrc.includes("cfg: t.cfg,") &&
  !extSrc.includes("hostAudioInstance = activeInstance().name;"));
ok("switch: registry membership is an OWN-property test (a webview name is untrusted)",
  extSrc.includes("if (!instances.hasInstance(reg, wanted)) {") &&
  !/reg\.byName\[wanted\]/.test(extSrc));

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

  // ── The per-instance coalescer (auto-import) ───────────────────────────────
  // The live failure: an automatic scan of A is still running when the window switches
  // to B, B's lifecycle pre-flight joins A's promise and treats B as scanned, and A's
  // throttle stamp suppresses B's first automatic scan for five minutes. Driven here
  // with deferred promises and a fake clock — nothing resolves until the test says so.
  console.log("\n=== per-instance import coalescing ===");
  let clock = 1000;
  const co = inst.createCoalescer({ throttleMs: 5 * 60 * 1000, now: () => clock });
  const started = [];
  /** Start a scan for a captured target; resolves with the cfg the scan actually got. */
  const scan = (target, force, d) => co.run(target.name, force, () => {
    started.push(target.name);
    return d.promise.then(() => ({ instance: target.name, cfg: target.cfg }));
  });
  const instA = { name: "agent-vm", cfg: inst.toSshCfg(d) };
  const instB = { name: "work-vm", cfg: inst.toSshCfg(workInst) };

  // (a) Two overlapping requests for A share ONE scan.
  const dA = deferred();
  const a1 = scan(instA, false, dA);
  const a2 = scan(instA, false, dA);
  ok("coalesce: a second request for A joins the in-flight scan", a1 === a2);
  eq("coalesce: ...and A's scan was started exactly once", started.filter((n) => n === "agent-vm").length, 1);

  // (b) A request for B while A is pending starts its OWN scan (this is the bug).
  const dB = deferred();
  const b1 = scan(instB, false, dB);
  ok("coalesce: B does not join A's in-flight scan", b1 !== a1);
  ok("coalesce: ...B's scan really started", started.includes("work-vm"));
  ok("coalesce: both keys are in flight at once", co.isInflight("agent-vm") && co.isInflight("work-vm"));

  // (d) Each scan gets the cfg of the instance it was captured for.
  dB.resolve(); dA.resolve();
  const [ra, rb] = [await a1, await b1];
  eq("coalesce: A's result carries A's cfg", ra.cfg.vmHost, instA.cfg.vmHost);
  eq("coalesce: B's result carries B's cfg", rb.cfg.vmHost, instB.cfg.vmHost);
  ok("coalesce: A's result is never B's", ra.instance === "agent-vm" && rb.instance === "work-vm" &&
    ra.cfg.vmHost !== rb.cfg.vmHost);
  ok("coalesce: settled scans release their keys",
    !co.isInflight("agent-vm") && !co.isInflight("work-vm"));

  // (c) The throttle is keyed too: A's stamp must not suppress B's FIRST scan.
  clock += 1000;
  const co2 = inst.createCoalescer({ throttleMs: 5 * 60 * 1000, now: () => clock });
  const startedT = [];
  const tScan = (name, force) => co2.run(name, force, () => { startedT.push(name); return Promise.resolve(name); });
  eq("throttle: A's first automatic scan runs", await tScan("agent-vm", false), "agent-vm");
  clock += 1000;                                   // still well inside the window
  eq("throttle: A's second automatic scan is suppressed", await tScan("agent-vm", false), null);
  eq("throttle: B's FIRST automatic scan still runs (A's stamp is not B's)",
    await tScan("work-vm", false), "work-vm");
  deepEq("throttle: exactly the two first scans ran", startedT, ["agent-vm", "work-vm"]);
  eq("throttle: force bypasses the window", await tScan("agent-vm", true), "agent-vm");
  clock += 5 * 60 * 1000 + 1;                      // the window elapses
  eq("throttle: an automatic scan runs again afterwards", await tScan("agent-vm", false), "agent-vm");
  // A failing scan must not leave the key stuck "in flight" forever.
  const boom = co2.run("boom-vm", true, () => Promise.reject(new Error("ssh died")));
  eq("coalesce: a rejected scan resolves to null", await boom, null);
  ok("coalesce: ...and releases its key", !co2.isInflight("boom-vm"));
  const throwing = co2.run("throw-vm", true, () => { throw new Error("sync boom"); });
  eq("coalesce: a synchronously throwing scan resolves to null", await throwing, null);

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
    instances: { "agent-vm": {}, "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 } },
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
    instances: { "agent-vm": {}, "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 } },
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

  // ── The keyed follow-up queue (config-sync ticks) ──────────────────────────
  // The real failure: A's tick is in flight, the user hits "Sync Now" for A, then
  // switches to B before the follow-up starts. Held as ONE global promise, that
  // follow-up synced B's branch and B's VM store while A's changes stayed unsynced.
  console.log("\n=== keyed follow-up queue (a queued tick keeps its target) ===");
  const qActive = deferred();
  const q = inst.createTargetQueue();
  const ran = [];
  // Two callers ask about A while A's tick runs: ONE follow-up, and it is A's.
  const qa1 = q.queue("agent-vm", qActive.promise, () => { ran.push("agent-vm"); return "synced agent-vm"; });
  const qa2 = q.queue("agent-vm", qActive.promise, () => { ran.push("agent-vm-2"); return "second closure"; });
  ok("queue: a second caller for the same target JOINS the queued follow-up", qa1 === qa2);
  // ...and a caller about B gets its own, because the two sync different branches.
  const qb1 = q.queue("work-vm", qActive.promise, () => { ran.push("work-vm"); return "synced work-vm"; });
  ok("queue: a different target gets its OWN follow-up", qb1 !== qa1);
  eq("queue: one entry per target", q.size, 2);
  ok("queue: nothing has run while the active tick is in flight", ran.length === 0);
  qActive.resolve(null);
  eq("queue: the follow-up runs the closure ITS caller registered", await qa1, "synced agent-vm");
  eq("queue: ...the joining caller sees the same result", await qa2, "synced agent-vm");
  eq("queue: ...and B's follow-up is B's", await qb1, "synced work-vm");
  deepEq("queue: the superseded second closure never ran", ran.sort(), ["agent-vm", "work-vm"]);
  ok("queue: the keys are released once their follow-up starts", q.size === 0);
  // A FAILING active tick must not wedge the queue: the follow-up still runs.
  const failing = Promise.reject(new Error("tick blew up"));
  failing.catch(() => {});   // the test process must not see an unhandled rejection
  const afterFail = await inst.createTargetQueue().queue("agent-vm", failing, () => "ran anyway");
  eq("queue: a rejected active tick still releases its follow-up", afterFail, "ran anyway");

  // ── Deferred steps decide with the target they captured ────────────────────
  // The reprovision toast the user answers minutes later, and the mic auto-arm's
  // reachability probe: both must refuse to act when the window switched meanwhile —
  // acting on the CURRENT instance is as wrong as acting on the captured one.
  console.log("\n=== deferred follow-ups (prompt answers, probe results) ===");
  const fgate = inst.createGate("agent-vm");
  const deferredA = inst.captureTarget(fgate, inst.DEFAULT_INSTANCE);
  eq("deferred: a declined prompt does nothing",
    inst.planCapturedFollowUp(fgate, deferredA, false).reason, "declined");
  const okPlan = inst.planCapturedFollowUp(fgate, deferredA, true);
  ok("deferred: an accepted prompt runs — for the CAPTURED target",
    okPlan.run && okPlan.reason === "ok" && okPlan.target === deferredA);
  fgate.set("work-vm");   // the window switches while the toast / probe is outstanding
  const stale = inst.planCapturedFollowUp(fgate, deferredA, true);
  ok("deferred: a stale accept neither runs nor retargets",
    !stale.run && stale.reason === "superseded" && stale.target === deferredA);
  eq("deferred: a stale DECLINE is still just a decline (nothing to explain)",
    inst.planCapturedFollowUp(fgate, deferredA, false).reason, "declined");
  ok("deferred: a target captured after the switch is live again",
    inst.planCapturedFollowUp(fgate, inst.captureTarget(fgate, inst.DEFAULT_INSTANCE), true).run);
  ok("deferred: no gate / no target is never treated as superseded",
    inst.planCapturedFollowUp(null, null, true).run);

  // ── The import tail: A's discoveries never reach B's settings file ─────────
  // importFromVm scans A over SSH and then auto-enables what it found into A's
  // .construct-settings.json. Both halves hang off ONE capture (instance + cfg +
  // scriptsDir + generation); the extension code is source-pinned above, and the RULE
  // it applies is driven here with a deferred scan, exactly as the live failure went:
  // the scan is slow, the user switches, and the write must land nowhere.
  console.log("\n=== captured import tail (a scan of A must not write B) ===");
  const files = { "agent-vm": [], "work-vm": [] };            // each VM's selection file
  const profileWrites = [];                                   // config-dir profile files
  const stamps = [];                                          // per-instance throttle
  const igate = inst.createGate("agent-vm");
  // The flow, written the way extension.js writes it: capture BEFORE the first await,
  // then check the generation again before EVERY mutation — the profile files and the
  // throttle stamp included, not just the settings write at the end.
  // `afterScan` is the test's deterministic hook for "the window switches HERE" — it
  // fires once the scan-stage check has passed, i.e. during the deletion-history read.
  const importFlow = async (target, scanPromise, historyPromise, afterScan) => {
    const found = await scanPromise;                          // the SSH scan of THAT VM
    if (!inst.planCapturedFollowUp(igate, target, true).run) return "discarded:scan";
    if (afterScan) afterScan();
    await historyPromise;                                     // the deletion-history read
    const plan = inst.planCapturedFollowUp(igate, target, found.length > 0);
    if (!plan.run) return "discarded:" + plan.reason;
    // Every write is addressed by the CAPTURE, never by "the active instance now".
    profileWrites.push(...found);                             // host.writeProjectProfileIfAbsent
    stamps.push(plan.target.name);                            // importCoalescer.stamp
    files[plan.target.scriptsDir].push(...found);             // host.saveSelectedProjects
    return "enabled";
  };
  const capturedImport = (name) => ({
    ...inst.captureTarget(igate, { name, ...inst.DEFAULT_INSTANCE }),
    name,
    scriptsDir: name,   // stands in for that instance's .construct-settings.json
  });
  // (i) the switch lands while the SSH scan is still in flight.
  const slowScan = deferred();
  const runImport = importFlow(capturedImport("agent-vm"), slowScan.promise, Promise.resolve());
  igate.set("work-vm");                                       // ...the user switches
  slowScan.resolve(["repo-from-a"]);                          // ...and A's scan lands
  eq("import: a scan that finishes after a switch is discarded", await runImport, "discarded:scan");
  deepEq("import: ...no profile file is written at all", profileWrites, []);
  deepEq("import: ...the per-instance throttle is not stamped", stamps, []);
  deepEq("import: ...B's selection file is untouched", files["work-vm"], []);
  deepEq("import: ...and A's is too (this window no longer drives it)", files["agent-vm"], []);
  // (ii) the scan came back in time, but the switch lands during the deletion-history
  //      read — the last await before the writes.
  const lateHistory = deferred();
  igate.set("agent-vm");
  const runLate = importFlow(capturedImport("agent-vm"), Promise.resolve(["repo-from-a"]),
    lateHistory.promise, () => igate.set("work-vm"));
  lateHistory.resolve(null);
  eq("import: a switch during the deletion-history read discards it too", await runLate, "discarded:superseded");
  deepEq("import: ...still nothing written", profileWrites, []);
  deepEq("import: ...still nothing stamped", stamps, []);
  deepEq("import: ...and neither selection file moved",
    files["agent-vm"].concat(files["work-vm"]), []);
  // The control: no switch, and the discovery lands in the scanned VM's own files.
  const okScan = deferred();
  const runImportB = importFlow(capturedImport("work-vm"), okScan.promise, Promise.resolve());
  okScan.resolve(["repo-from-b"]);
  eq("import: without a switch the discovery is enabled", await runImportB, "enabled");
  deepEq("import: ...the profile is written once", profileWrites, ["repo-from-b"]);
  deepEq("import: ...the SCANNED instance is the one stamped", stamps, ["work-vm"]);
  deepEq("import: ...in the SCANNED instance's file only", files["work-vm"], ["repo-from-b"]);
  deepEq("import: ...and nothing leaked into the other one", files["agent-vm"], []);

  // ── A queued follow-up that goes stale touches NOTHING ─────────────────────
  console.log("\n=== stale queued follow-up ===");
  const sgate = inst.createGate("agent-vm");
  const sq = inst.createTargetQueue();
  const touched = [];
  const staleTarget = { ...inst.captureTarget(sgate, inst.DEFAULT_INSTANCE) };
  const activeTick = deferred();
  const queued = sq.queue(staleTarget.name, activeTick.promise, () => {
    // The follow-up re-checks its generation before it starts a tick of its own.
    const plan = inst.planCapturedFollowUp(sgate, staleTarget, true);
    if (!plan.run) return "aborted:" + plan.reason;
    touched.push(plan.target.name);
    return "synced";
  });
  sgate.set("work-vm");            // the switch happens inside the queue window
  activeTick.resolve(null);
  eq("queue: a stale follow-up aborts instead of running", await queued, "aborted:superseded");
  deepEq("queue: ...and syncs NEITHER instance", touched, []);

  // ── Per-target sync STATUS and THROTTLE (A's tick must not speak for B) ────
  // The live failure: A syncs, the user switches to B, and the panel shows A's
  // timestamp/result/warnings/blocked reason under B's name while A's stamp satisfies the
  // window-global 5-minute throttle — so B's first automatic tick never runs and B's
  // branch and VM store stay unsynchronized. Modelled here the way extension.js is now
  // wired: the STATUS and the THROTTLE are keyed by the captured target, while the
  // in-flight tick and its follow-up queue stay global (one config repo, one lock).
  console.log("\n=== per-target config-sync status + throttle ===");
  // The pure mapping first (extension.js used to inline it twice).
  eq("status: a fresh target has never synced", inst.describeSyncStatus(null, null).lastResult, null);
  eq("status: ok", inst.describeSyncStatus(1, { ok: true }).lastResult, "ok");
  eq("status: conflict", inst.describeSyncStatus(1, { conflict: true }).lastResult, "conflict");
  eq("status: blocked", inst.describeSyncStatus(1, { blocked: true, blockedReason: "why" }).blockedReason, "why");
  eq("status: anything else is an error", inst.describeSyncStatus(1, {}).lastResult, "error");
  deepEq("status: warnings default to an empty list", inst.describeSyncStatus(1, { ok: true }).warnings, []);
  eq("status: a 0 timestamp reads as never (unchanged from the old global)",
    inst.describeSyncStatus(0, { ok: true }).lastSyncAt, null);

  let syncClock = 10000;
  const status = inst.createSyncStatusStore({ throttleMs: 5 * 60 * 1000, now: () => syncClock });
  const tgate = inst.createGate("agent-vm");
  // The window-global halves, exactly as extension.js holds them.
  let inFlight = null;
  const tickQueue = inst.createTargetQueue();
  const ticked = [];                                   // {name, branch}, in run order
  const runTick = (target, d) => {
    // A tick that arrives while another is running queues behind it — repo-wide, for
    // EITHER instance, because both write the same config repo under the same lock.
    if (inFlight) return tickQueue.queue(target.name, inFlight, () => runTick(target, d));
    const p = d.promise.then((result) => {
      status.record(target.name, result);              // <- keyed by the CAPTURED target
      ticked.push({ name: target.name, branch: target.instance.configBranch });
      if (inFlight === p) inFlight = null;
      return result;
    });
    inFlight = p;
    return p;
  };
  const maybeAuto = (target, d) =>
    (status.dueForAuto(target.name) ? runTick(target, d) : Promise.resolve("throttled"));
  const capturedSync = (name, branch) => ({
    ...inst.captureTarget(tgate, { name, configBranch: branch }),
    instance: { name, configBranch: branch },
  });

  const syncTA = capturedSync("agent-vm", "vm");
  const syncDA = deferred();
  const syncRunA = maybeAuto(syncTA, syncDA);                      // A has never synced -> it runs
  // The user switches to B while A's tick is still in flight.
  tgate.set("work-vm");
  const syncTB = capturedSync("work-vm", "vm-work-vm");
  const syncDB = deferred();
  const syncRunB = maybeAuto(syncTB, syncDB);
  ok("sync state: B's tick is DUE (A's stamp does not throttle it)", status.dueForAuto("work-vm"));
  ok("sync state: ...but it queues behind A's — one repo, one lock", tickQueue.isQueued("work-vm"));
  deepEq("sync state: nothing has ticked yet", ticked, []);
  syncDA.resolve({ ok: true, warnings: ["A's warning"] });
  await syncRunA;
  // A's tick has landed. B's panel state must still say "never synced" — A's timestamp,
  // result and warnings belong to A's branch and A's VM store.
  const bBefore = status.status("work-vm");
  eq("sync state: B reports no last sync of its own", bBefore.lastSyncAt, null);
  eq("sync state: ...no result", bBefore.lastResult, null);
  deepEq("sync state: ...and none of A's warnings", bBefore.warnings, []);
  const aAfter = status.status("agent-vm");
  eq("sync state: A reports its own result", aAfter.lastResult, "ok");
  eq("sync state: ...at its own timestamp", aAfter.lastSyncAt, 10000);
  syncClock += 60000;                                   // one minute later
  ok("sync state: A is inside its own throttle window", !status.dueForAuto("agent-vm"));
  ok("sync state: B is STILL due — A's tick never suppressed it", status.dueForAuto("work-vm"));
  syncDB.resolve({ ok: false, blocked: true, blockedReason: "lock busy", warnings: ["B's warning"] });
  await syncRunB;
  deepEq("sync state: both ticks ran, in lock order", ticked.map((t) => t.name), ["agent-vm", "work-vm"]);
  deepEq("sync state: ...each on its OWN branch", ticked.map((t) => t.branch), ["vm", "vm-work-vm"]);
  const bAfter = status.status("work-vm");
  eq("sync state: B now reports B's result", bAfter.lastResult, "blocked");
  eq("sync state: ...B's blocked reason", bAfter.blockedReason, "lock busy");
  deepEq("sync state: ...and B's warnings", bAfter.warnings, ["B's warning"]);
  eq("sync state: ...at B's own timestamp", bAfter.lastSyncAt, 70000);
  // Switching back must not have disturbed A's entry either.
  tgate.set("agent-vm");
  const aBack = status.status("agent-vm");
  eq("sync state: A's status survives the round trip", aBack.lastResult, "ok");
  deepEq("sync state: ...with A's warnings, not B's", aBack.warnings, ["A's warning"]);
  eq("sync state: ...and A's timestamp, not B's", aBack.lastSyncAt, 10000);
  eq("sync state: one entry per instance", status.size, 2);
  // Once B's window elapses its automatic tick runs again — for B alone.
  syncClock += 5 * 60 * 1000;
  ok("sync state: B is due again after its OWN window", status.dueForAuto("work-vm"));
  eq("sync state: a target that never synced is always due",
    await maybeAuto(capturedSync("third-vm", "vm-third-vm"), { promise: Promise.resolve({ ok: true }) }).then((r) => r.ok),
    true);

  // ── The merge gate WRITES a branch, so it is bounded the same way ──────────
  // completePendingMerge creates the merge commit whose message names the branch, and
  // the gate's verdict decides whether a destructive rebuild may proceed. Both halves
  // are modelled here: the write must not happen on a stale capture, and the verdict a
  // stale gate returns must never be "clear" (a pre-flight has to fail CLOSED).
  console.log("\n=== captured merge gate (branch write + fail-closed verdict) ===");
  const mgate = inst.createGate("agent-vm");
  const merges = [];                                          // branches merged, in order
  // `afterMerge` is the "switch HERE" hook: after the branch write, during the repo read.
  const mergeGate = async (target, detectPromise, repoPromise, afterMerge) => {
    await detectPromise;                                      // git detection
    if (!inst.planCapturedFollowUp(mgate, target, true).run) return { blocked: true, stale: true };
    merges.push(target.instance.configBranch);                // completePendingMerge
    if (afterMerge) afterMerge();
    const rs = await repoPromise;                             // repoState
    if (!inst.planCapturedFollowUp(mgate, target, true).run) return { blocked: true, stale: true };
    return { blocked: !!rs.conflict, stale: false };
  };
  const capturedGate = (name, branch) => ({
    ...inst.captureTarget(mgate, { name, configBranch: branch }),
    instance: { name, configBranch: branch },
  });
  const slowDetect = deferred();
  const gateRun = mergeGate(capturedGate("agent-vm", "vm"), slowDetect.promise, Promise.resolve({}));
  mgate.set("work-vm");                                       // the switch beats git home
  slowDetect.resolve(null);
  const gateRes = await gateRun;
  ok("merge gate: a stale gate reports blocked+stale, never a clear verdict",
    gateRes.blocked === true && gateRes.stale === true);
  deepEq("merge gate: ...and no branch was merged at all", merges, []);
  // A switch AFTER the merge but before the verdict must still not read as "clear".
  const slowRepo = deferred();
  mgate.set("agent-vm");
  const lateGate = mergeGate(capturedGate("agent-vm", "vm"), Promise.resolve(), slowRepo.promise,
    () => mgate.set("work-vm"));
  slowRepo.resolve({ conflict: false });
  const lateRes = await lateGate;
  ok("merge gate: a switch after the write still fails closed", lateRes.blocked && lateRes.stale);
  deepEq("merge gate: ...on the CAPTURED branch, never the new instance's", merges, ["vm"]);
  // The control: no switch — the captured branch is merged and the verdict is honest.
  mgate.set("agent-vm");
  const clean = await mergeGate(capturedGate("agent-vm", "vm"), Promise.resolve(), Promise.resolve({ conflict: false }));
  ok("merge gate: without a switch the verdict is the repo's own", !clean.blocked && !clean.stale);
  deepEq("merge gate: ...and it merged the captured branch", merges, ["vm", "vm"]);
}

asyncTests().then(() => {
  try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (_) {}
  console.log(`\ninstance-registry unit tests — ${pass}/${pass + fail} passed`);
  if (fail) process.exit(1);
}, (e) => {
  console.log("  FAIL  async tests threw: " + (e && e.stack ? e.stack : e));
  process.exit(1);
});
