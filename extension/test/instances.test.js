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
const { EventEmitter } = require("events");
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
// ...and SKIPPED, not left actionable with the derived <name>.mshome.net address (a
// remote endpoint only ever comes from the host service).
ok("messy: hyperv-remote without sshHost is skipped", !messy.byName["remote-vm"]);

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
// `martian-remote` is an UNKNOWN backend on purpose: it is neither local (so the
// canonical-identity rule does not apply) nor remote (so the endpoint/vmName rules do
// not), which leaves exactly the field-by-field TYPE normalization on show. The
// uppercase spelling of a KNOWN backend has its own fixture below, because a spelling
// getDriver() resolves to the remote driver is held to that backend's rules.
const mx = inst.load({ path: writeRegistry(JSON.stringify({
  version: 1,
  instances: {
    "typed-vm": {
      backend: "martian-remote", sshHost: 123, sshPort: "2201",
      hostAlias: true, keyName: 42, configBranch: ["x"], owner: 7,
      service: { url: "https://x", auth: "TOKEN" },
    },
    "svc-vm": { service: "not-an-object" },
    "port-vm": { sshPort: "+2201" },
  },
})) });
const tv = mx.byName["typed-vm"];
eq("parity: unknown backend kept verbatim", tv.backend, "martian-remote");
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
ok("parity: unknown backend reported", mx.problems.some((p) => p.includes("martian-remote")));
ok("parity: uppercase auth reported", mx.problems.some((p) => p.includes("service auth")));
ok("parity: scalar service reported", mx.problems.some((p) => p.includes('"service" must be an object')));
ok("parity: bad port reported", mx.problems.some((p) => p.includes("invalid sshPort")));

// A CASE-VARIANT of a known backend id does not load AT ALL, for either backend. Every
// enum comparison in both readers is case-sensitive (so "HYPERV-REMOTE" is "unknown" to
// them), while getDriver() trims and lowercases (so it hands back the REAL driver — the
// remote one here, the local one with hostLifecycle for "HYPERV-LOCAL"). The two
// readings disagree about what the entry IS, so nothing acts on it under either.
for (const [label, id] of [["remote", "HYPERV-REMOTE"], ["local", "HYPERV-LOCAL"], ["mixed", "Hyperv-Remote"]]) {
  const cased = inst.load({ path: writeRegistry(
    '{"version":1,"instances":{"cased-vm":{"backend":"' + id + '","sshHost":"buildbox.local","sshPort":2201}}}') });
  ok(`parity: a case-variant backend id (${label}) does not load`, !cased.byName["cased-vm"]);
  ok(`parity: ...reported as a spelling the two lookups read differently (${label})`,
    cased.problems.some((p) => p.includes(id) && p.includes("case-sensitive") && p.includes("skipped")));
  ok(`parity: ...naming the canonical spelling (${label})`,
    cased.problems.some((p) => p.includes('is not spelled "' + id.toLowerCase() + '"')));
}
// ...while a genuinely unknown id IS kept: getDriver finds no driver for it under any
// casing, so the unknown-driver fallback is what acts on it (hypervisor actions refused).
ok("parity: an unknown backend still loads and is merely reported",
  !!inst.load({ path: writeRegistry(
    '{"version":1,"instances":{"cased-vm":{"backend":"proxmox","sshHost":"buildbox.local"}}}') }).byName["cased-vm"]);

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
  // `git check-ref-format --branch vm-work.` fails, so a hand-authored entry ending in a
  // dot must be refused HERE — accepting it meant the branch was only created (or not) at
  // the first sync tick. Same fixture in test/instances.test.ps1.
  ["branch-trailing-dot", { configBranch: "vm-work." }, "configBranch"],
  // A branch is a FILE too on the host: the config repo keeps loose refs on Windows, so
  // refs/heads/CON (and the CON.lock git writes beside it) cannot be created — the same
  // device rule keyName gets. Linux git accepts these, so only the validator catches them.
  ["branch-device-con", { configBranch: "CON" }, "configBranch"],
  ["branch-device-nul", { configBranch: "nul" }, "configBranch"],
  ["branch-device-com1", { configBranch: "COM1" }, "configBranch"],
  ["branch-device-with-extension", { configBranch: "CON.txt" }, "configBranch"],
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
  // The device rule is the STEM, not a substring: these are ordinary branch names.
  { configBranch: "console" }, { configBranch: "con-work" },
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
// correctly (unknownDriver), which is what refuses the destructive actions. (A
// case-variant of a KNOWN id — "HYPERV-REMOTE" — is NOT in this list: getDriver
// lowercases, so that one really does get a driver, and it is refused whole instead. See
// the case-variant fixtures in the parity matrix above.)
for (const backend of ["proxmox", "hyperv-remtoe", "HYPERV-PROXMOX"]) {
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

// ── The REMOTE backend's own identity rules ─────────────────────────────────
// Mirrored assertion-for-assertion in test/instances.test.ps1 (same fixtures, same
// order); change the two together.
//   vmName === name — the host service addresses the VM by that name, and so does a
//     rebuild (-InstanceName). An entry keyed `alias-vm` with vmName `service-vm` had
//     Start and the power state acting on service-vm while Reinstall DELETED and
//     recreated alias-vm.
//   sshHost stated  — a remote endpoint is whatever the service allocated. An entry
//     that omits it used to load with the DERIVED `<name>.mshome.net:22`, i.e. an
//     actionable instance pointing at an unrelated machine on this PC's own network.
console.log("\n=== canonical remote identity (vmName === name, sshHost required) ===");
const REMOTE_OK = { backend: "hyperv-remote", vmName: "work-vm", sshHost: "buildbox.example.local", sshPort: 2201 };
const remoteGood = parse({ version: 1, instances: { "work-vm": REMOTE_OK } });
deepEq("remote: a canonical remote entry loads with no problems", remoteGood.problems, []);
eq("remote: ...as itself", remoteGood.registry.byName["work-vm"].vmHost, "buildbox.example.local");
// An omitted vmName DERIVES the instance name, so it satisfies the rule by construction.
ok("remote: an omitted vmName derives the instance name and is fine",
  !!parse({ version: 1, instances: { "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.local" } } })
    .registry.byName["work-vm"]);
for (const [label, entry, field] of [
  ["a service VM name of its own", { ...REMOTE_OK, vmName: "service-vm" }, "vmName"],
  // Compared EXACTLY: the value goes into a URL path and into a -InstanceName argument,
  // and nothing may assume the service folds case.
  ["a differently-cased vmName", { ...REMOTE_OK, vmName: "Work-VM" }, "vmName"],
  ["no sshHost at all", { backend: "hyperv-remote", sshPort: 2201 }, "sshHost"],
  ["an empty sshHost", { backend: "hyperv-remote", sshHost: "   ", sshPort: 2201 }, "sshHost"],
  // The canonical spelling is `sshHost` — everything that writes the registry writes it.
  ["the endpoint under the vmHost alias only", { backend: "hyperv-remote", vmHost: "buildbox.local" }, "sshHost"],
]) {
  const parsed = parse({ version: 1, instances: { "work-vm": entry } });
  ok(`remote(${label}): the entry is ABSENT, not merely warned about`, !parsed.registry.byName["work-vm"]);
  ok(`remote(${label}): the problem names "${field}" and says skipped`,
    parsed.problems.some((p) => p.includes(`"${field}"`) && p.includes("skipped")));
  // ...so nothing can act on it: resolve falls back to the default instance.
  ok(`remote(${label}): resolve falls back to the default instance`,
    inst.isDefaultInstance(inst.resolve(parsed.registry, "work-vm")));
}
// The derived mshome endpoint is exactly what must NOT survive a missing sshHost.
const remoteNoHost = parse({ version: 1, instances: { "work-vm": { backend: "hyperv-remote" } } });
ok("remote: no entry is left holding the derived <name>.mshome.net address",
  !remoteNoHost.registry.byName["work-vm"] &&
  !inst.list(remoteNoHost.registry).some((i) => i.vmHost === "work-vm.mshome.net"));
ok("remote: the missing endpoint is reported in the words the PS reader uses",
  remoteNoHost.problems.some((p) => p.includes("no sshHost")));
// The WRITE side refuses the same shapes where they are created (they would otherwise be
// persisted and then vanish from the picker on the next load).
const remoteBase = inst.load({ path: writeRegistry(JSON.stringify({ version: 1, instances: {} })) });
ok("remote: addInstance accepts the canonical entry",
  !!inst.addInstance(remoteBase, "work-vm", REMOTE_OK).byName["work-vm"]);
for (const [label, entry] of [
  ["a service VM name of its own", { ...REMOTE_OK, vmName: "service-vm" }],
  ["no sshHost", { backend: "hyperv-remote", sshPort: 2201 }],
]) {
  ok(`remote: addInstance refuses ${label}`,
    (() => { try { inst.addInstance(remoteBase, "work-vm", entry); return false; } catch (_) { return true; } })());
}
ok("remote: updateInstance refuses a patch that splits the identity",
  (() => {
    const withRemote = inst.addInstance(remoteBase, "work-vm", REMOTE_OK);
    try { inst.updateInstance(withRemote, "work-vm", { vmName: "service-vm" }); return false; } catch (_) { return true; }
  })());
ok("remote: updateInstance refuses a patch that removes the endpoint",
  (() => {
    const withRemote = inst.addInstance(remoteBase, "work-vm", REMOTE_OK);
    try { inst.updateInstance(withRemote, "work-vm", { sshHost: null }); return false; } catch (_) { return true; }
  })());
// The rules are the remote backend's ALONE: a local instance's vmName is its own
// (case-insensitive) rule and needs no sshHost at all.
ok("remote: the rules do not touch a hyperv-local entry",
  !!parse({ version: 1, instances: { "work-vm": { backend: "hyperv-local", vmName: "Work-VM" } } })
    .registry.byName["work-vm"]);

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

// ── The endpoint identity is (sshHost, sshPort), not the host alone ──────────
// Several hyperv-remote VMs legitimately live on ONE service host and are told apart by
// the SSH forward the service allocated them (plan §4.4: one port per VM from a
// configured range). Keyed on the host alone, every VM on a shared host collided and the
// "drop BOTH" rule then lost the whole registry. Mirrored in test/instances.test.ps1.
console.log("\n=== endpoint uniqueness: (sshHost, sshPort) ===");
const sameHost = parse({ version: 1, instances: {
  "a-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 },
  "b-vm": { backend: "hyperv-remote", sshHost: "BuildBox.Example.local", sshPort: 2202 },
} });
ok("endpoint: two remote VMs on ONE service host, different ports, both load",
  !!sameHost.registry.byName["a-vm"] && !!sameHost.registry.byName["b-vm"]);
deepEq("endpoint: ...with nothing reported", sameHost.problems, []);
const samePort = parse({ version: 1, instances: {
  "a-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 },
  "b-vm": { backend: "hyperv-remote", sshHost: "BuildBox.Example.local", sshPort: 2201 },
} });
ok("endpoint: the SAME host and port is still one machine — both dropped",
  !samePort.registry.byName["a-vm"] && !samePort.registry.byName["b-vm"]);
ok("endpoint: ...reported once, naming both and the host:port",
  samePort.problems.filter((p) => p.includes("share the same sshHost/sshPort")).length === 1 &&
  samePort.problems.some((p) => p.includes("a-vm") && p.includes("b-vm") &&
    p.includes("buildbox.example.local:2201")));
ok("endpoint: the port comparison is numeric, so \"2201\" and 2201 are one endpoint",
  !parse({ version: 1, instances: {
    "a-vm": { backend: "hyperv-remote", sshHost: "one.local", sshPort: 2201 },
    "b-vm": { backend: "hyperv-remote", sshHost: "one.local", sshPort: "2201" },
  } }).registry.byName["a-vm"]);
// A local instance's port is canonically 22 and its host derives from its own name, so
// local entries still cannot share an endpoint — but a remote VM reached through a
// FORWARD on the same machine is a different endpoint and must load.
const localAndRemote = parse({ version: 1, instances: {
  "work-vm": { backend: "hyperv-remote", sshHost: "agent-vm.mshome.net", sshPort: 2201 },
} });
ok("endpoint: a remote VM forwarded on the default VM's host (other port) loads",
  !!localAndRemote.registry.byName["work-vm"]);
deepEq("endpoint: ...and nothing is reported", localAndRemote.problems, []);
ok("endpoint: the default instance survives it untouched",
  inst.isDefaultInstance(localAndRemote.registry.byName["agent-vm"]));
ok("endpoint: ...while the default instance's OWN host:port is still reserved",
  !parse({ version: 1, instances: {
    "work-vm": { backend: "hyperv-remote", sshHost: "agent-vm.mshome.net", sshPort: 22 },
  } }).registry.byName["work-vm"]);
// Sharing a host must not loosen ANY other identity.
ok("endpoint: a shared host does not excuse a shared key file",
  !parse({ version: 1, instances: {
    "a-vm": { backend: "hyperv-remote", sshHost: "one.local", sshPort: 2201, keyName: "shared_key" },
    "b-vm": { backend: "hyperv-remote", sshHost: "one.local", sshPort: 2202, keyName: "Shared_Key" },
  } }).registry.byName["a-vm"]);
// The write side agrees with the reader, in both directions.
const epBase = parse({ version: 1, instances: {
  "a-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 },
} }).registry;
ok("endpoint: addInstance accepts a second VM on the same host, another port",
  !!inst.addInstance(epBase, "b-vm", { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2202 })
    .byName["b-vm"]);
ok("endpoint: addInstance refuses a second VM on the same host AND port",
  (() => {
    try { inst.addInstance(epBase, "b-vm", { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 }); return false; }
    catch (e) { return /sshHost\/sshPort/.test(e.message); }
  })());

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
// The ENDPOINT is (sshHost, sshPort): the default VM's HOST on another port is a
// different endpoint (nothing of the default instance is claimed), its host on port 22
// is the default instance's own and is refused.
ok("update: a remote instance may keep the default VM's host on another port",
  inst.updateInstance(added, "work-vm", { sshHost: "agent-vm.mshome.net" })
    .byName["work-vm"].vmHost === "agent-vm.mshome.net");
ok("update: rejects a change that would collide with the default instance's endpoint",
  (() => { try { inst.updateInstance(added, "work-vm", { sshHost: "agent-vm.mshome.net", sshPort: 22 }); return false; } catch (_) { return true; } })());
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
const REMOTE_SVC = { url: "https://buildbox.example.local:7462", auth: "negotiate" };
const remoteInst = inst.deriveDefaults("work-vm", {
  backend: "hyperv-remote", sshHost: "buildbox.local", sshPort: 2201, service: REMOTE_SVC,
});
eq("gate: the fixture really is hyperv-remote", remoteInst.backend, "hyperv-remote");
// B7: a remote instance CAN be rebuilt — Auto-Install.ps1 gained the remote path — but
// checkpoints stay refused, because the backend has none.
const REMOTE_ALL = ["Backend", "ServiceUrl", "InstanceName", "ConfigBranch", "VmHost", "HostAlias", "SshPort", "LocalKeyName", "VmName"];
for (const action of ["reinstall", "redownload"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, instance: remoteInst, instanceParams: REMOTE_ALL });
  ok(`gate: ${action} is ALLOWED for a hyperv-remote instance`, !r.blocked && r.script === life.AUTO_INSTALL);
  ok(`gate: ${action} targets the SERVICE, not a local VM name`,
    r.args.includes("-Backend") && r.args.includes("hyperv-remote") &&
    r.args.includes("-ServiceUrl") && r.args.includes(REMOTE_SVC.url) &&
    r.args.includes("-InstanceName") && r.args.includes("work-vm") &&
    !r.args.includes("-VmName"));
}
// ...and the name it targets on the service is the SAME string the driver queries and
// starts. That is what the canonical remote identity buys: `-InstanceName` (the rebuild,
// which DELETES) and drivers/hyperv-remote.vmNameOf (the power state, Start) can no
// longer name two different VMs on the host.
const remoteLoaded = inst.resolve(parse({ version: 1, instances: {
  "work-vm": { backend: "hyperv-remote", vmName: "work-vm", sshHost: "buildbox.local", sshPort: 2201, service: REMOTE_SVC },
} }).registry, "work-vm");
const remoteRebuild = life.buildInvocation("reinstall", { settings: SETTINGS, instance: remoteLoaded, instanceParams: REMOTE_ALL });
eq("gate: the rebuild's -InstanceName IS the name the driver drives",
  remoteRebuild.args[remoteRebuild.args.indexOf("-InstanceName") + 1],
  require("../src/drivers/hyperv-remote").vmNameOf(remoteLoaded));
const chk = life.buildInvocation("setCheckpoints", { settings: SETTINGS, enabled: true, instance: remoteInst, instanceParams: REMOTE_ALL });
ok("gate: setCheckpoints is refused for a hyperv-remote instance", chk.blocked === true);
ok("gate: ...because the backend has no checkpoints", /no checkpoints/i.test(chk.reason));
// A remote entry with NO service.url cannot be rebuilt on any version of the scripts:
// there is nothing to ask for a VM, and an Auto-Install without -ServiceUrl runs local.
const servicelessInst = inst.deriveDefaults("work-vm", { backend: "hyperv-remote", sshHost: "buildbox.local" });
for (const action of ["reinstall", "redownload"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, instance: servicelessInst, instanceParams: REMOTE_ALL });
  ok(`gate: ${action} is refused when the entry records no host service`, r.blocked === true);
  ok(`gate: ...and says so (${action})`, /host service/i.test(r.reason));
}
// Version skew fails CLOSED: scripts that predate the remote parameters would run the
// LOCAL path and rebuild a local VM named after the remote one.
for (const action of ["reinstall", "redownload"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, instance: remoteInst, instanceParams: ["ConfigBranch"] });
  ok(`gate: ${action} fails closed on an Auto-Install without -ServiceUrl`, r.blocked === true);
  ok(`gate: ...and names the missing parameters (${action})`,
    /-Backend/.test(r.reason) && /-ServiceUrl/.test(r.reason) && /-InstanceName/.test(r.reason));
}
for (const action of ["reprovision", "exportConfig"]) {
  const r = life.buildInvocation(action, { settings: SETTINGS, backupDir: "C:\\b", instance: remoteInst, instanceParams: ALL });
  ok(`gate: ${action} stays ALLOWED for hyperv-remote (pure SSH to the VM)`, !r.blocked && !!r.script);
  ok(`gate: ${action} still targets the remote endpoint`, r.args.includes("buildbox.local"));
  ok(`gate: ${action} is unchanged for a remote instance (endpoint identity, no service args)`,
    !r.args.includes("-ServiceUrl") && !r.args.includes("-Backend"));
}
// An unknown backend (a registry written by a newer Construct) is still refused.
const alienInst = inst.deriveDefaults("work-vm", { sshHost: "buildbox.local" });
alienInst.backend = "proxmox";
ok("gate: an unknown backend is refused the hypervisor actions",
  life.buildInvocation("reinstall", { settings: {}, instance: alienInst, instanceParams: ALL }).blocked === true);
ok("gate: drivers.lifecycleSupport is the single source of truth",
  drivers.lifecycleSupport("hyperv-local", "reinstall").ok === true &&
  drivers.lifecycleSupport("hyperv-remote", "reinstall").ok === true &&
  drivers.lifecycleSupport("hyperv-remote", "setCheckpoints").ok === false &&
  drivers.lifecycleSupport("proxmox", "reinstall").ok === false &&
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

// The mic tunnel's HANDOVER across a switch. The integration review found three ways
// the old shape (disable-if-a-tunnel-exists, then arm concurrently) lost the new VM's
// tunnel; the ordering rules themselves are modelled with deferred promises in
// asyncTests() below (createHandover / createSessionOwner).
ok("mic switch: the destination is ALWAYS evaluated, through ONE serialized handover",
  extSrc.includes("const audioHandover = instances.createHandover({") &&
  extSrc.includes("session: () => ({ live: !!hostAudio, name: hostAudioInstance }),") &&
  extSrc.includes("teardown: () => disableAudio(),") &&
  extSrc.includes("arm: (target) => maybeAutoEnableAudio(extensionContext, target),") &&
  extSrc.includes("superseded: (target) => instances.targetSuperseded(instanceGate, target),") &&
  extSrc.includes("void audioHandover.switch(instances.captureTarget(instanceGate, inst));") &&
  // ...and the old gate — which armed NOTHING when the previous instance never produced
  // a tunnel — is gone.
  !extSrc.includes("if (hostAudio && hostAudioInstance && hostAudioInstance !== inst.name) {"));
ok("mic switch: ...only when the destination really changed (a single-VM window never re-arms)",
  extSrc.includes("const micSwitched = inst.name !== audioTargetInstance;") &&
  extSrc.includes("if (micSwitched) {") &&
  extSrc.includes("audioTargetInstance = inst.name;") &&
  // the startup arm records the instance it evaluated, so the first onInstanceChanged
  // with an unchanged name is a no-op rather than a fresh arm.
  extSrc.includes("audioTargetInstance = activeInstance().name;") &&
  extSrc.indexOf("audioTargetInstance = activeInstance().name;") <
    extSrc.indexOf("void requestAudioEnable(context, undefined, { auto: true });"));
ok("mic switch: the teardown is AWAITABLE, so the destination's arm is sequenced after it",
  extSrc.includes("if (!hostAudio) { broadcastAudio({ enabled: false, capturing: false }); return Promise.resolve(); }") &&
  extSrc.includes("return Promise.resolve(vscode.window.withProgress(") &&
  extSrc.includes(")).catch(() => {});"));
ok("mic switch: the teardown also waits for an enable still IN FLIGHT on that session",
  // one enable promise per session, published before anything can await it...
  extSrc.includes("const started = hostAudio.enable();") &&
  extSrc.includes("hostAudioEnable = Promise.resolve(started).then(() => null, () => null);") &&
  extSrc.includes("return started.then(handle, () => handle({ ok: false, error: \"enable-failed\" })).catch(() => {});") &&
  extSrc.includes("async () => { handle(await started); }") &&
  // ...and the teardown captures it and awaits it BEFORE disabling the session, so a
  // half-enabled HostAudio can't finish its tunnel behind the teardown's back.
  extSrc.includes("const pendingEnable = hostAudioEnable;") &&
  extSrc.includes("if (pendingEnable) await pendingEnable;") &&
  extSrc.indexOf("if (pendingEnable) await pendingEnable;") < extSrc.indexOf("const r = await inst.disable();") &&
  !/hostAudio\.enable\(\)\.then\(/.test(extSrc));
ok("mic switch: every status a session emits carries its slot claim, and a superseded one is dropped",
  extSrc.includes("const audioSlot = instances.createSessionOwner();") &&
  extSrc.includes("const session = audioSlot.claim(t.name);") &&
  extSrc.includes("hostAudioSession = session;") &&
  extSrc.includes("onStatus: (s) => broadcastAudio(s, session),") &&
  extSrc.includes("function broadcastAudio(status, session) {") &&
  extSrc.includes("if (session != null && !audioSlot.owns(session)) {") &&
  // the teardown reports for the session it tore down, not for whatever is armed now
  extSrc.includes("const session = hostAudioSession;") &&
  extSrc.includes("broadcastAudio({ enabled: false, capturing: false }, session);"));
ok("mic switch: a superseded enable RESULT cannot clear the new tunnel's reference",
  extSrc.includes("if (!audioSlot.owns(session)) {") &&
  extSrc.indexOf("if (!audioSlot.owns(session)) {") < extSrc.indexOf("      // Reset the switch to off on every surface."));
// ...and the MANUAL operations ride the SAME chain. Run beside it, a manual enable that
// arrived while a switch was tearing the previous instance down saw no session (the
// reference was already dropped), built one, and the switch's own arm then built a
// SECOND HostAudio for the same VM — the newer claim replaced the module's reference,
// the older object's result was discarded as superseded, and nothing was left that could
// dispose it: an orphan `ssh -R`. The ordering is modelled with deferred promises in
// asyncTests() (createHandover.enable / .disable).
ok("mic: every enable and the manual disable are queued on the ONE session chain",
  extSrc.includes("function requestAudioEnable(context, webview, opts = {}) {") &&
  extSrc.includes("return audioHandover.enable(target, (t) => (") &&
  extSrc.includes("opts.auto ? maybeAutoEnableAudio(context, t) : enableAudio(context, webview, { target: t })") &&
  extSrc.includes("function requestAudioDisable() {") &&
  extSrc.includes("return audioHandover.disable();") &&
  // ...and NOTHING calls the raw enable/disable outside that chain any more (the
  // handover's own teardown, and the auto-arm the chain invokes, are the exceptions).
  extSrc.includes("if (message.enabled) void requestAudioEnable(context, webview);") &&
  extSrc.includes("else void requestAudioDisable();") &&
  extSrc.includes("if (wantMic && !micOn) void requestAudioEnable(context, webview);") &&
  extSrc.includes("else if (!wantMic && micOn) void requestAudioDisable();") &&
  extSrc.includes("void requestAudioEnable(context, undefined, { auto: true, target: t });") &&
  // ...and the raw teardown has exactly TWO mentions left: its own definition and the
  // handover's `teardown` hook. Every user-facing "off" goes through the chain.
  extSrc.split("disableAudio()").length === 3);
// The DECISION itself is the pure instances.planEnable (exercised on its own, and driven
// through the deferred model, in asyncTests()); extension.js only supplies the state and
// carries out the answer. What is pinned here is that wiring.
ok("mic: the enable decision is taken by instances.planEnable, from one state helper",
  extSrc.includes("const plan = instances.planEnable(audioSlotState(), t.name);") &&
  extSrc.includes("function audioSlotState() {") &&
  extSrc.includes("live: !!hostAudio,") && extSrc.includes("name: hostAudioInstance,") &&
  extSrc.includes("enabled: !!(hostAudio && hostAudio.enabled),") &&
  extSrc.includes("pending: !!hostAudioEnable,") && extSrc.includes("closed: audioHandover.closed,") &&
  // "report" and the non-"create" answers both return WITHOUT reaching the constructor,
  // so nothing can build a second HostAudio while one is held.
  extSrc.includes('if (plan.action === "report") {') &&
  extSrc.includes('if (plan.action !== "create") {') &&
  extSrc.indexOf('if (plan.action !== "create") {') < extSrc.indexOf("hostAudio = new audio.HostAudio({") &&
  extSrc.includes('const pending = plan.action === "join" ? hostAudioEnable : null;') &&
  extSrc.includes("return Promise.resolve(pending).then(() => { reportAudioState(webview); });") &&
  extSrc.includes("function reportAudioState(webview) {"));
ok("mic: shutdown closes the chain BEFORE the one disposal that happens outside it",
  extSrc.includes("try { void audioHandover.close(); } catch (_) {}") &&
  extSrc.indexOf("void audioHandover.close();") < extSrc.indexOf("try { if (hostAudio) hostAudio.dispose(); } catch (_) {}"));
ok("switch: a rejected workspaceState write installs a window-local override, so the message is true",
  extSrc.includes("const persistence = instances.planSwitchPersistence(wanted, persisted, pin);") &&
  // the pin is read BEFORE the report, so a pinned window is never told it switched
  extSrc.indexOf("const setting = instanceSetting();") < extSrc.indexOf("const persistence = instances.planSwitchPersistence(") &&
  extSrc.includes("windowInstanceOverride = persistence.override;") &&
  extSrc.includes("if (persistence.message) vscode.window.showWarningMessage(persistence.message);") &&
  extSrc.includes("if (windowInstanceOverride) return windowInstanceOverride;") &&
  // the old copy claimed a switch "for now" while nothing held the new selection
  !extSrc.includes("for now, but the choice couldn't be saved for this window"));
ok("switch: BOTH warnings key off the EFFECTIVE pin (a stale setting pins nothing)",
  extSrc.includes("const pin = instances.effectivePin(reg, setting);") &&
  extSrc.includes("if (pin && pin !== wanted) {") &&
  extSrc.includes('pins every window to "${pin}"') &&
  // ...and a setting the registry doesn't hold is logged rather than reported as a pin
  extSrc.includes("if (setting && !pin) {") &&
  !/setting && setting !== wanted/.test(extSrc));

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

  // ── The mic tunnel's handover across a switch ──────────────────────────────
  // Three live failures the integration review found in the old shape (arm only when a
  // tunnel for another instance existed, then tear down and arm CONCURRENTLY):
  //   1. instance A never produced a tunnel (unreachable at startup) ⇒ switching to B
  //      never even looked at B's saved micPassthrough;
  //   2. A's teardown finished after B's arm and its trailing "disabled" status painted
  //      B's live tunnel off — and a late enable result of A's cleared the module's
  //      reference to B's HostAudio, leaking B's tunnel;
  //   3. A→B→C superseded B's arm mid-flight with nothing sequencing the teardowns.
  console.log("\n=== mic tunnel handover (no prior tunnel / delayed teardown / A->B->C) ===");
  // The pure decision first.
  deepEq("handover: with NO live session the destination is still evaluated",
    inst.planHandover({ live: false, name: null, next: "work-vm" }), { teardown: false, arm: true });
  deepEq("handover: a session on ANOTHER VM is torn down, and the destination evaluated",
    inst.planHandover({ live: true, name: "agent-vm", next: "work-vm" }), { teardown: true, arm: true });
  deepEq("handover: a session that already belongs to the destination is left alone",
    inst.planHandover({ live: true, name: "work-vm", next: "work-vm" }), { teardown: false, arm: false });
  deepEq("handover: names compare case-sensitively, like every instance comparison here",
    inst.planHandover({ live: true, name: "Work-VM", next: "work-vm" }), { teardown: true, arm: true });

  // The SINGLE-SESSION rule, as its own pure decision (instances.planEnable) — the
  // branch that used to live inline in extension.js's enableAudio. Every state the mic
  // slot can be in, and what an explicit "on" must do about it.
  deepEq("planEnable: nothing held -> build the session (the single-VM path)",
    inst.planEnable({ live: false }, "agent-vm"), { action: "create", reason: "idle" });
  deepEq("planEnable: a session that is already ENABLED is only re-reported",
    inst.planEnable({ live: true, name: "agent-vm", enabled: true }, "agent-vm"),
    { action: "report", reason: "already-enabled" });
  deepEq("planEnable: a session still being enabled is JOINED, never replaced",
    inst.planEnable({ live: true, name: "work-vm", pending: true }, "work-vm"),
    { action: "join", reason: "pending" });
  deepEq("planEnable: ...even when it belongs to ANOTHER instance (the fail-closed backstop)",
    inst.planEnable({ live: true, name: "agent-vm", pending: true }, "work-vm"),
    { action: "join", reason: "pending-other-instance" });
  deepEq("planEnable: a held session with no enable to join is REFUSED, not built over",
    inst.planEnable({ live: true, name: "work-vm" }, "work-vm"), { action: "refuse", reason: "held" });
  // Shutdown outranks every other state: after the chain is closed nothing new may exist.
  for (const [label, state] of [
    ["with nothing held", { live: false, closed: true }],
    ["with a session coming up", { live: true, name: "work-vm", pending: true, closed: true }],
    ["with a live session", { live: true, name: "work-vm", enabled: true, closed: true }],
  ]) {
    deepEq(`planEnable: a CLOSED window refuses to enable (${label})`,
      inst.planEnable(state, "work-vm"), { action: "refuse", reason: "closed" });
  }
  deepEq("planEnable: a missing state is the idle one", inst.planEnable(null, "agent-vm"),
    { action: "create", reason: "idle" });

  // The slot owner: a later claim invalidates every earlier one; an unclaimed status
  // (the single-VM disable, which claims nothing new) still goes out.
  const owner = inst.createSessionOwner();
  ok("slot: nothing owns the slot before a claim", !owner.owns(1) && owner.id === null && owner.name === null);
  const claimA = owner.claim("agent-vm");
  ok("slot: the claim owns the slot", owner.owns(claimA) && owner.id === claimA && owner.name === "agent-vm");
  const claimB = owner.claim("work-vm");
  ok("slot: a later claim supersedes the earlier one", owner.owns(claimB) && !owner.owns(claimA));
  ok("slot: a missing id never owns the slot", !owner.owns(null) && !owner.owns(undefined) && !owner.owns(0));

  // A MODEL of extension.js's mic wiring — the module-level hostAudio /
  // hostAudioInstance / hostAudioSession, the slot owner, the gated broadcast, the
  // awaitable teardown, the captured-target arm and onInstanceChanged's "did the
  // destination really change" guard — driven with deferred promises so the
  // interleavings are exact instead of intermittent in a live window.
  const tick = async (n) => { for (let i = 0; i < (n || 4); i++) await Promise.resolve(); };
  function micWindow(startName, opts) {
    // `joinPendingEnables: false` reproduces the pre-fix shape for the control below: the
    // decision is taken WITHOUT instances.planEnable, exactly as extension.js used to
    // take it inline ("nothing is enabled right now, so build one").
    const joinPending = !(opts && opts.joinPendingEnables === false);
    const gate = inst.createGate(startName);
    const slot = inst.createSessionOwner();
    // The module-level state: hostAudio / hostAudioInstance / hostAudioSession, plus
    // hostAudioEnable — the enable that is still in flight ON that session, which is what
    // a second enable has to JOIN rather than replace.
    const mic = { tunnel: null, name: null, session: null, enable: null };
    let evaluated = startName;                                  // audioTargetInstance
    let teardown = null;                                        // the pending VM cleanup
    const log = [], shown = [], tunnels = [], probes = new Map();
    const probeFor = (name) => {
      if (!probes.has(name)) probes.set(name, deferred());
      return probes.get(name);
    };
    const broadcast = (status, session) => {
      if (session != null && !slot.owns(session)) { log.push("status-dropped:" + status.from); return; }
      shown.push(status);
    };
    const target = (name) => ({ ...inst.captureTarget(gate, { name }), name });
    /** extension.js's audioSlotState(): the module state planEnable reads. */
    const slotState = () => ({
      live: !!mic.tunnel,
      name: mic.name,
      enabled: !!(mic.tunnel && mic.tunnel.enabled),
      pending: !!mic.enable,
      closed: handover.closed,
    });
    // enableAudio(): asks the PRODUCTION decision (instances.planEnable) what to do about
    // the session that exists right now, then carries the answer out — claim the slot and
    // open the tunnel, whose RESULT lands later through that same claim. The model
    // supplies only the effects; the branch under test is the real one.
    const enableAudio = (t) => {
      const plan = joinPending
        ? inst.planEnable(slotState(), t.name)
        // The PRE-FIX inline decision, for the control: only an already-ENABLED session
        // stopped a second one from being built.
        : { action: mic.tunnel && mic.tunnel.enabled ? "report" : "create", reason: "pre-fix" };
      log.push("plan:" + plan.action + ":" + t.name);
      if (plan.action === "report") {
        broadcast({ from: mic.name, enabled: true });
        return Promise.resolve();
      }
      if (plan.action !== "create") {
        // "join" awaits the enable that is already running — the promise the module
        // published for that session — and reports on what it produced. "refuse" has
        // nothing to await (a closed window, or a session with no enable in flight).
        const pending = plan.action === "join" ? mic.enable : null;
        return Promise.resolve(pending).then(() => {
          broadcast({ from: mic.name, enabled: !!(mic.tunnel && mic.tunnel.enabled) });
        });
      }
      if (inst.targetSuperseded(gate, t)) { log.push("enable-discarded:" + t.name); return Promise.resolve(); }
      const session = slot.claim(t.name);
      const tunnel = { name: t.name, session, open: true, enabled: false, result: deferred() };
      tunnels.push(tunnel);
      mic.tunnel = tunnel; mic.name = t.name; mic.session = session;
      log.push("armed:" + t.name);
      // hostAudioEnable: one promise per session, published before anything can await it.
      const pending = tunnel.result.promise.then(() => null, () => null);
      mic.enable = pending;
      tunnel.result.promise.then((okResult) => {
        if (!slot.owns(session)) { log.push("result-dropped:" + tunnel.name); return; }
        if (okResult) { tunnel.enabled = true; return; }
        mic.tunnel = null; mic.name = null; mic.session = null; mic.enable = null;
        tunnel.open = false;
        broadcast({ from: tunnel.name, enabled: false }, session);
      });
      // The step ends when the session is PUBLISHED, not when its enable settles — that
      // is deliberately where this model draws the boundary, because the window between
      // the two is exactly where a second enable used to slip in. (extension.js returns
      // the enable itself, so its steps additionally wait for the tunnel to come up: a
      // superset of this ordering, driven against the real HostAudio further down.)
      return Promise.resolve();
    };
    // maybeAutoEnableAudio(): capture -> probe -> planCapturedFollowUp -> enable.
    const arm = async (t) => {
      const reachable = await probeFor(t.name).promise;
      const plan = inst.planCapturedFollowUp(gate, t, reachable);
      if (!plan.run) { log.push("arm-" + plan.reason + ":" + t.name); return; }
      return enableAudio(plan.target);
    };
    // disableAudio(): drops the reference at once, finishes over SSH, and reports for
    // the session it tore down (never for whatever is armed by then).
    const disable = () => {
      if (!mic.tunnel) { broadcast({ from: "(none)", enabled: false }); return Promise.resolve(); }
      const tunnel = mic.tunnel, session = mic.session;
      mic.tunnel = null; mic.name = null; mic.session = null; mic.enable = null;
      teardown = deferred();
      log.push("teardown-started:" + tunnel.name);
      return teardown.promise.then(() => {
        tunnel.open = false;
        log.push("teardown-done:" + tunnel.name);
        broadcast({ from: tunnel.name, enabled: false }, session);
      });
    };
    const handover = inst.createHandover({
      session: () => ({ live: !!mic.tunnel, name: mic.name }),
      teardown: disable,
      arm,
      superseded: (t) => inst.targetSuperseded(gate, t),
    });
    return {
      mic, log, shown, tunnels,
      probe: (name, reachable) => probeFor(name).resolve(reachable),
      finishTeardown: () => { const d = teardown; teardown = null; d.resolve(); return d.promise; },
      /** activate()'s startup auto-arm (before it, too, was queued on the chain). */
      armStartup: (name) => arm(target(name)),
      /** requestAudioEnable(): the console/settings toggle's "on", on the ONE chain. */
      manualEnable: (name) => handover.enable(target(name), (t) => enableAudio(t)),
      /** requestAudioEnable({auto}): the startup / repatch auto-arm, on the same chain. */
      autoArm: (name) => handover.enable(target(name), (t) => arm(t)),
      /** requestAudioDisable(): the toggle's "off", queued so it cannot overtake an "on". */
      manualDisable: () => handover.disable(),
      /** The PRE-FIX shape, for the control: an enable run BESIDE the chain. */
      enableOffChain: (name) => enableAudio(target(name)),
      /** deactivate()'s first act: close the chain (nothing may construct after it). */
      close: () => handover.close(),
      /** HostAudio.enable() settling for the newest session of `name`. */
      settleEnable(name, okResult) {
        const t = tunnels.filter((x) => x.name === name).slice(-1)[0];
        t.result.resolve(okResult !== false);
        return t.result.promise;
      },
      /** onInstanceChanged()'s mic half, guard included. */
      switchTo(name) {
        gate.set(name);
        if (name === evaluated) { log.push("no-op:" + name); return Promise.resolve({ armed: false, reason: "unchanged" }); }
        evaluated = name;
        return handover.switch(target(name));
      },
      broadcast,
      openTunnels: () => tunnels.filter((t) => t.open).map((t) => t.name),
      /** Tunnels that are still OPEN while the module no longer references them — the
       *  leak this whole chain exists to make impossible. */
      orphans: () => tunnels.filter((t) => t.open && t !== mic.tunnel).map((t) => t.name),
      builtFor: (name) => tunnels.filter((t) => t.name === name).length,
    };
  }

  // 1) A switch with NO prior tunnel still arms the destination. The old gate armed
  //    nothing here, so a window that started on an unreachable A never honoured B's
  //    saved micPassthrough at all.
  const w1 = micWindow("agent-vm");
  w1.probe("work-vm", true);
  const r1 = await w1.switchTo("work-vm");
  ok("no prior tunnel: the destination is evaluated and armed",
    r1.armed === true && r1.teardown === false && r1.reason === "armed");
  deepEq("no prior tunnel: ...and the tunnel that opened is the DESTINATION's", w1.openTunnels(), ["work-vm"]);

  // 2) A slow teardown of A: B is armed only after A has let go, and A's later status
  //    (and its later enable result) cannot speak for B.
  const w2 = micWindow("agent-vm");
  w2.probe("agent-vm", true);
  await w2.armStartup("agent-vm");
  const tunnelA = w2.tunnels[0];
  deepEq("delayed teardown: A's tunnel is up to begin with", w2.openTunnels(), ["agent-vm"]);
  const s2 = w2.switchTo("work-vm");
  w2.probe("work-vm", true);
  await tick();
  ok("delayed teardown: B is NOT armed while A is still tearing down",
    w2.log.includes("teardown-started:agent-vm") && !w2.log.includes("armed:work-vm"));
  await w2.finishTeardown();
  const r2 = await s2;
  ok("delayed teardown: B is armed only after A let go",
    r2.armed === true && w2.log.indexOf("teardown-done:agent-vm") < w2.log.indexOf("armed:work-vm"));
  deepEq("delayed teardown: exactly one tunnel is open, and it is B's", w2.openTunnels(), ["work-vm"]);
  const shownBefore = w2.shown.length;
  w2.broadcast({ from: "agent-vm", enabled: false }, tunnelA.session);   // A's late onStatus
  eq("delayed teardown: A's late status is dropped, not painted over B's", w2.shown.length, shownBefore);
  ok("delayed teardown: ...and it is recorded as dropped", w2.log.includes("status-dropped:agent-vm"));
  tunnelA.result.resolve(false);                                        // A's late enable result
  await tick();
  eq("delayed teardown: a late FAILED enable of A leaves B's tunnel referenced", w2.mic.name, "work-vm");
  deepEq("delayed teardown: ...so nothing leaks", w2.openTunnels(), ["work-vm"]);
  ok("delayed teardown: ...and A's result was discarded", w2.log.includes("result-dropped:agent-vm"));

  // 3) A->B->C while A's teardown is still running: C is armed, B's arm is superseded
  //    cleanly, and no tunnel is left behind.
  const w3 = micWindow("agent-vm");
  w3.probe("agent-vm", true);
  await w3.armStartup("agent-vm");
  const s3b = w3.switchTo("work-vm");
  await tick();
  const s3c = w3.switchTo("third-vm");
  w3.probe("work-vm", true);
  w3.probe("third-vm", true);
  await w3.finishTeardown();
  const r3b = await s3b, r3c = await s3c;
  eq("A->B->C: B's arm is superseded, cleanly", r3b.reason, "superseded");
  ok("A->B->C: ...and B never opened a tunnel", !w3.log.includes("armed:work-vm"));
  eq("A->B->C: C is the one that gets armed", r3c.reason, "armed");
  deepEq("A->B->C: exactly one tunnel is open, and it is C's", w3.openTunnels(), ["third-vm"]);
  ok("A->B->C: A was torn down exactly once", w3.log.filter((l) => l === "teardown-started:agent-vm").length === 1);

  // 4) The control — a single-VM window never switches, so nothing new happens: no
  //    teardown, no second arm, byte-identical to the pre-instances behaviour.
  const w4 = micWindow("agent-vm");
  w4.probe("agent-vm", true);
  await w4.armStartup("agent-vm");
  const r4 = await w4.switchTo("agent-vm");      // e.g. construct.instance edited to the same name
  eq("single VM: a 'switch' to the instance already active does nothing", r4.reason, "unchanged");
  deepEq("single VM: ...one tunnel, still A's", w4.openTunnels(), ["agent-vm"]);
  deepEq("single VM: ...and the window did exactly one thing all session",
    w4.log, ["plan:create:agent-vm", "armed:agent-vm", "no-op:agent-vm"]);

  // ...and if a handover DOES run for an instance that already holds the tunnel, it
  // leaves it alone rather than cycling the mic.
  const idle = inst.createHandover({
    session: () => ({ live: true, name: "work-vm" }),
    teardown: () => { throw new Error("must not tear down the destination's own tunnel"); },
    arm: () => { throw new Error("must not re-arm a live tunnel"); },
    superseded: () => false,
  });
  const idleRes = await idle.switch({ name: "work-vm" });
  ok("handover: a destination that already holds the tunnel is left alone",
    idleRes.reason === "already-armed" && idleRes.armed === false && idleRes.teardown === false);
  // A teardown that FAILS (an unreachable VM) must not wedge the chain — the destination
  // still gets its tunnel.
  const wedged = inst.createHandover({
    session: () => ({ live: true, name: "agent-vm" }),
    teardown: () => Promise.reject(new Error("ssh died")),
    arm: () => Promise.resolve(),
    superseded: () => false,
  });
  const wedgedRes = await wedged.switch({ name: "work-vm" });
  ok("handover: a FAILED teardown still hands over", wedgedRes.armed === true && wedgedRes.teardown === true);

  // ── A MANUAL enable racing the serialized handover ────────────────────────
  // The integration review's exact interleaving: A's enable is still in flight, the
  // window switches to B (which clears the reference and waits for A), and the user then
  // flips the console switch for B DURING that wait. Run beside the chain, that manual
  // enable saw no session, built one, and B's queued auto-arm then built a SECOND
  // HostAudio for the same VM — the newer claim took the slot, the first enable's result
  // was discarded as superseded, and nothing was left that could disable it: a live
  // `ssh -R` to B with no reference to tear it down. On the chain it can only join.
  console.log("\n=== manual enable vs. the serialized handover (one session, zero orphans) ===");
  const w5 = micWindow("agent-vm");
  w5.probe("agent-vm", true);
  await w5.armStartup("agent-vm");            // A's session exists; its enable never settles
  const s5 = w5.switchTo("work-vm");          // A's teardown starts, B's arm queued behind it
  await tick();
  const m5 = w5.manualEnable("work-vm");      // the user flips the switch mid-teardown
  w5.probe("work-vm", true);
  await tick();
  ok("manual+auto: the manual enable WAITS in the queue while A tears down",
    w5.log.includes("teardown-started:agent-vm") &&
    !w5.log.includes("armed:work-vm") && !w5.log.includes("plan:join:work-vm"));
  await w5.finishTeardown();
  await tick(12);
  ok("manual+auto: the switch's arm opened B's tunnel, and the manual enable JOINED it",
    w5.log.indexOf("armed:work-vm") >= 0 &&
    w5.log.indexOf("armed:work-vm") < w5.log.indexOf("plan:join:work-vm"));
  // The join really does await the enable that was already in flight — it is still
  // waiting on B's HostAudio.enable(), which has not settled yet.
  let joinSettled = false;
  void m5.then(() => { joinSettled = true; });
  await tick();
  ok("manual+auto: ...and it is still awaiting THAT enable, not reporting its own",
    joinSettled === false && w5.builtFor("work-vm") === 1);
  w5.settleEnable("work-vm", true);           // B's enable finally lands
  const r5 = await s5, rm5 = await m5;
  eq("manual+auto: the switch's own arm is what opened B's tunnel", r5.reason, "armed");
  eq("manual+auto: ...and the manual enable is reported as a join", rm5.reason, "joined");
  eq("manual+auto: exactly ONE session was ever built for B", w5.builtFor("work-vm"), 1);
  deepEq("manual+auto: one tunnel is open, and it is B's", w5.openTunnels(), ["work-vm"]);
  deepEq("manual+auto: nothing is orphaned", w5.orphans(), []);

  // The reverse order — the manual enable gets there first and the auto-arm follows
  // (startup, or the repatch retry): the arm must join, not build a second session.
  const w6 = micWindow("work-vm");
  const m6 = w6.manualEnable("work-vm");
  const a6 = w6.autoArm("work-vm");
  w6.probe("work-vm", true);
  await tick(12);
  ok("auto after manual: the auto-arm joins the session the manual enable is building",
    w6.log.includes("plan:join:work-vm") && w6.builtFor("work-vm") === 1);
  w6.settleEnable("work-vm", true);
  const rm6 = await m6, ra6 = await a6;
  eq("auto after manual: the manual enable built the session", rm6.reason, "armed");
  eq("auto after manual: ...and the auto-arm joined it", ra6.reason, "joined");
  eq("auto after manual: one session for the destination", w6.builtFor("work-vm"), 1);
  deepEq("auto after manual: one tunnel, no orphans", w6.openTunnels(), ["work-vm"]);
  deepEq("auto after manual: ...", w6.orphans(), []);

  // ...and an "off" that arrives while an "on" is still coming up runs AFTER it, so the
  // tunnel the enable opens is the one the disable tears down — not one left behind.
  const w7 = micWindow("work-vm");
  const on7 = w7.manualEnable("work-vm");
  const off7 = w7.manualDisable();
  await tick(12);
  ok("off after on: the off waits for the on, so it tears down the session that was opened",
    w7.log.indexOf("armed:work-vm") >= 0 &&
    w7.log.indexOf("armed:work-vm") < w7.log.indexOf("teardown-started:work-vm"));
  await w7.finishTeardown();
  const r7 = await off7;
  await on7;
  eq("off after on: the disable reports the teardown it really did", r7.reason, "torn-down");
  deepEq("off after on: no tunnel is left open", w7.openTunnels(), []);
  deepEq("off after on: ...and nothing is orphaned", w7.orphans(), []);
  // The control: an "off" with nothing armed still reports "there is nothing armed" —
  // the single-VM path's own behaviour, unchanged.
  const w8 = micWindow("agent-vm");
  const r8 = await w8.manualDisable();
  eq("off with nothing armed: reported as idle", r8.reason, "idle");
  eq("off with nothing armed: ...and the status still goes out", w8.shown.length, 1);

  // ── Shutdown: deactivate() CLOSES the chain, and nothing can build after it ────
  // deactivate() disposes the one live session directly (it cannot await SSH), so the
  // rule that only the chain constructs has to hold across that moment: close() first,
  // dispose second. Two ways a session could otherwise appear behind the dispose.
  console.log("\n=== shutdown (the chain is closed before the one direct disposal) ===");
  const w10 = micWindow("agent-vm");
  w10.close();                                 // deactivate()'s first act
  const r10 = await w10.manualEnable("agent-vm");
  eq("shutdown: a step queued after close() is refused", r10.reason, "closed");
  deepEq("shutdown: ...and its enable never ran at all", w10.log, []);
  deepEq("shutdown: ...so no tunnel was built", w10.openTunnels(), []);
  const r10b = await w10.manualDisable();
  eq("shutdown: a queued teardown is refused too (the direct dispose owns it)", r10b.reason, "closed");
  // ...and a step that was ALREADY RUNNING when the window closed — an auto-arm sitting
  // in its reachability probe — refuses to construct on the way out.
  const w11 = micWindow("agent-vm");
  const arming11 = w11.autoArm("agent-vm");
  await tick();
  ok("shutdown: the arm is in flight (waiting on its probe)", w11.builtFor("agent-vm") === 0);
  w11.close();                                 // deactivate() while the probe is out
  w11.probe("agent-vm", true);                 // the VM answers AFTER the window closed
  await arming11;
  ok("shutdown: an in-flight arm refuses to build a session after close()",
    w11.log.includes("plan:refuse:agent-vm") && w11.builtFor("agent-vm") === 0);
  deepEq("shutdown: ...so nothing is left open behind the dispose", w11.openTunnels(), []);
  // The control: without the close, that same late probe DOES open a tunnel — which is
  // exactly what would have survived deactivate().
  const w12 = micWindow("agent-vm");
  const arming12 = w12.autoArm("agent-vm");
  await tick();
  w12.probe("agent-vm", true);
  await arming12;
  deepEq("shutdown control: an unclosed window's late arm opens its tunnel", w12.openTunnels(), ["agent-vm"]);

  // The CONTROL — the shape the review found: the manual enable runs BESIDE the chain and
  // does not join a session that is still coming up. Two HostAudios for one VM, the first
  // one's result discarded as superseded, and nothing left that can dispose it.
  const w9 = micWindow("agent-vm", { joinPendingEnables: false });
  w9.probe("agent-vm", true);
  await w9.armStartup("agent-vm");
  const s9 = w9.switchTo("work-vm");
  await tick();
  w9.enableOffChain("work-vm");               // the manual toggle, unqueued
  w9.probe("work-vm", true);
  await w9.finishTeardown();
  await s9;
  eq("control: the unqueued shape builds TWO sessions for one VM", w9.builtFor("work-vm"), 2);
  deepEq("control: ...and leaves the first one open with nothing referencing it", w9.orphans(), ["work-vm"]);

  // ── A switch while the previous instance's enable is STILL IN FLIGHT ───────
  // The model above opens its tunnel synchronously, so it cannot see this ordering:
  // HostAudio.disable() tears down what exists WHEN IT RUNS, and mid-enable that is
  // nothing yet — the enable then opens its AudioSession and `ssh -R` after the teardown
  // has already passed, leaving a live tunnel on the instance we left that no reference
  // can reach. Driven against the REAL src/audio.js HostAudio with its ssh runner, net
  // server and spawner injected, paused before its first awaited SSH result.
  console.log("\n=== mid-enable switch (the real HostAudio, paused before its first SSH result) ===");
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  function fakeHostAudio(label, spawns, opts) {
    const o = opts || {};
    const gateEnable = deferred();          // releases the enable's remote step
    // Optionally pause the LOCAL SERVER's listen too, so a cancellation arriving at that
    // later await can be driven as exactly as the SSH one.
    const gateListen = o.pauseListen ? deferred() : null;
    const netState = { closed: false, port: null };
    const server = new EventEmitter();
    server.listen = (port, host, cb) => {
      netState.port = 55555;
      if (cb) { if (gateListen) void gateListen.promise.then(() => cb()); else setImmediate(cb); }
      return server;
    };
    server.address = () => ({ port: netState.port });
    server.close = () => { netState.closed = true; };
    let sshCalls = 0;
    const h = new audio.HostAudio({
      cfg: { vmHost: label + ".mshome.net", hostAlias: label, keyName: "construct_" + label + "_ed25519" },
      _ssh: {
        resolveCfg: ssh.resolveCfg,
        keyPath: ssh.keyPath,
        runRemoteScript: () => {
          sshCalls += 1;
          // The FIRST call is the enable's remote step: it hangs until the test releases
          // it. Everything after (the disable script) answers at once.
          if (sshCalls === 1) return gateEnable.promise.then(() => ({ code: 0, stdout: "", stderr: "" }));
          return Promise.resolve({ code: 0, stdout: "", stderr: "" });
        },
      },
      _net: { createServer: () => server },
      _spawn: () => {
        const child = new EventEmitter();
        child.killed = false;
        child.kill = () => { child.killed = true; };
        spawns.push({ label, child });
        return child;
      },
      _readScript: (b) => "# " + b + "\n",
      _hasKey: () => true,
      mic: () => () => {},
      onStatus: () => {},
      _tunnelSettleMs: o.settleMs != null ? o.settleMs : 1,
    });
    return { h, gateEnable, gateListen, netState, remoteCalls: () => sshCalls };
  }

  const spawns = [];
  const sessionA = fakeHostAudio("agent-vm", spawns);
  // extension.js's enableAudio: ONE enable promise per session, published (mapped to
  // always settle) before anything can await it.
  const startedA = sessionA.h.enable();
  const enableA = Promise.resolve(startedA).then(() => null, () => null);
  await tick();
  ok("mid-enable: A's enable is paused before its first SSH result",
    spawns.length === 0 && sessionA.h.enabled === false);
  // ...and extension.js's disableAudio: wait for that enable to settle, THEN disable.
  const teardownA = (async () => { await enableA; return sessionA.h.disable(); })();
  sessionA.gateEnable.resolve();
  const rA = await teardownA;
  ok("mid-enable: the enable finished first, so its tunnel existed to tear down", spawns.length === 1);
  ok("mid-enable: ...and the teardown killed it", spawns[0].child.killed === true);
  ok("mid-enable: ...closed A's local server", sessionA.netState.closed === true);
  eq("mid-enable: ...and A is off", sessionA.h.enabled, false);
  ok("mid-enable: ...having run the VM-side cleanup", rA.ok === true && rA.was === true);
  // Only NOW is the destination armed — and it is the only live tunnel in the window.
  const sessionB = fakeHostAudio("work-vm", spawns);
  sessionB.gateEnable.resolve();
  const rB = await sessionB.h.enable();
  const live = spawns.filter((s) => !s.child.killed);
  ok("mid-enable: after the sequenced teardown exactly ONE tunnel is live", rB.ok === true && live.length === 1);
  eq("mid-enable: ...and it is the destination's", live[0].label, "work-vm");

  // The control — the ordering the sequencing prevents: disabling a half-enabled
  // HostAudio (what a concurrent teardown did) leaves the enable free to open its
  // tunnel afterwards, with the session already dropped from the module.
  const raceSpawns = [];
  const orphan = fakeHostAudio("agent-vm", raceSpawns);
  orphan.h.enable();                       // paused mid-enable, NOT waited for
  await tick();
  await orphan.h.disable();                // tears down what exists now: nothing
  orphan.gateEnable.resolve();
  await sleep(20);                         // let the enable finish behind the teardown
  ok("mid-enable control: disabling mid-enable lets the enable open a tunnel anyway",
    raceSpawns.length === 1 && raceSpawns[0].child.killed === false && orphan.h.enabled === true);
  orphan.h.dispose();                      // don't leave the fake child behind

  // ── deactivate() DURING an enable: the real HostAudio must not finish behind it ──
  // Closing the session chain stops everything QUEUED and everything that has not yet
  // reached the constructor, but it cannot retroactively stop an enable that is already
  // sitting in an SSH call: dispose() tears down what exists at that moment, and
  // mid-enable that is nothing. Without a cancellation flag the continuation went on to
  // bind its local server and spawn `ssh -R` AFTER the only reference to the object had
  // been dropped — a live tunnel this extension host could never stop again. So
  // HostAudio.enable() re-checks `_disposed` after EVERY await; these drive the real
  // object through each of those points.
  console.log("\n=== deactivate mid-enable (the real HostAudio, cancelled after each await) ===");
  // (a) cancelled while the VM was answering the remote enable.
  const cancelSpawns = [];
  const cancelA = fakeHostAudio("agent-vm", cancelSpawns);
  const cancelAEnable = cancelA.h.enable();
  await tick();
  ok("dispose mid-enable: the enable is paused in its first SSH call", cancelSpawns.length === 0);
  cancelA.h.dispose();                     // deactivate(): chain closed, then this
  cancelA.gateEnable.resolve();            // the VM answers AFTER the window went away
  const cancelARes = await cancelAEnable;
  eq("dispose mid-enable: the enable reports it was cancelled", cancelARes.error, "disposed");
  ok("dispose mid-enable: ...it never became enabled", cancelA.h.enabled === false && cancelARes.ok === false);
  ok("dispose mid-enable: ...no local server was ever bound", cancelA.netState.port === null);
  ok("dispose mid-enable: ...and no tunnel child was spawned", cancelSpawns.length === 0);
  // ...and the VM is not left mutated: step 1 installed the shim + patch, so the abort
  // runs the same guarded remote disable every other rollback path uses.
  ok("dispose mid-enable: ...while the VM-side shim/patch are reverted", cancelA.remoteCalls() >= 2);

  // (b) cancelled at the NEXT await — the local server's listen — where a server does
  //     exist by the time dispose lands.
  const cancelSpawnsB = [];
  const cancelB = fakeHostAudio("work-vm", cancelSpawnsB, { pauseListen: true });
  const cancelBEnable = cancelB.h.enable();
  cancelB.gateEnable.resolve();            // the VM answers; the enable moves to step 2
  await tick(6);
  ok("dispose mid-listen: the enable is paused binding its local server", cancelSpawnsB.length === 0);
  cancelB.h.dispose();                     // deactivate() lands in that window
  cancelB.gateListen.resolve();            // the server finishes binding afterwards
  const cancelBRes = await cancelBEnable;
  eq("dispose mid-listen: the enable reports it was cancelled", cancelBRes.error, "disposed");
  ok("dispose mid-listen: ...the server it bound is closed again", cancelB.netState.closed === true);
  ok("dispose mid-listen: ...no tunnel child was spawned", cancelSpawnsB.length === 0);
  eq("dispose mid-listen: ...and it is not enabled", cancelB.h.enabled, false);

  // (c) cancelled in the TUNNEL's settle window — the last await, and the only one where
  //     an `ssh -R` child already exists when dispose lands.
  const cancelSpawnsC = [];
  const cancelC = fakeHostAudio("third-vm", cancelSpawnsC, { settleMs: 200 });
  const cancelCEnable = cancelC.h.enable();
  cancelC.gateEnable.resolve();
  await sleep(20);                         // the child is spawned, the window still open
  ok("dispose mid-tunnel: the ssh -R child exists and is alive", cancelSpawnsC.length === 1);
  cancelC.h.dispose();                     // deactivate() during the settle window
  const cancelCRes = await cancelCEnable;
  eq("dispose mid-tunnel: the enable reports it was cancelled", cancelCRes.error, "disposed");
  ok("dispose mid-tunnel: ...the tunnel child it spawned is killed", cancelSpawnsC[0].child.killed === true);
  ok("dispose mid-tunnel: ...its server is closed", cancelC.netState.closed === true);
  ok("dispose mid-tunnel: ...and nothing reports as enabled",
    cancelC.h.enabled === false && cancelCRes.ok === false);

  // (c) the CONTROL — the same interleaving with no dispose at all really does open the
  //     tunnel, which is exactly what used to survive deactivate().
  const liveSpawns = [];
  const notCancelled = fakeHostAudio("work-vm", liveSpawns);
  notCancelled.gateEnable.resolve();
  const liveRes = await notCancelled.h.enable();
  ok("dispose control: without the cancellation the enable opens its tunnel",
    liveRes.ok === true && liveSpawns.length === 1 && liveSpawns[0].child.killed === false);
  // ...and disposing it afterwards is the ordinary path: the child is killed, the server closed.
  notCancelled.h.dispose();
  ok("dispose after enable: the tunnel child is killed and the server closed",
    liveSpawns[0].child.killed === true && notCancelled.netState.closed === true);
  // A disposed object refuses to be re-enabled at all (nothing may resurrect it).
  const reRes = await notCancelled.h.enable();
  eq("dispose: a disposed HostAudio refuses a later enable", reRes.error, "disposed");

  // ── A switch whose workspaceState write REJECTS ────────────────────────────
  // The old catch said the window had switched "for now" while nothing held the new
  // selection: activeInstance() kept resolving the PREVIOUS instance and the refresh that
  // followed re-rendered the VM the user had just switched away from.
  console.log("\n=== switch persistence (rejected workspaceState.update) ===");
  const swReg = inst.load({ path: writeRegistry(JSON.stringify({
    version: 1,
    defaultInstance: "agent-vm",
    instances: {
      "agent-vm": { backend: "hyperv-local" },
      "work-vm": { backend: "hyperv-remote", sshHost: "buildbox.example.local", sshPort: 2201 },
    },
  })) });
  // The window: workspaceState + the in-memory override, resolved exactly as
  // extension.js resolves them (setting > workspaceState/override > registry default).
  const switchWindow = async (storageOk, settingRaw) => {
    const setting = settingRaw || "";
    // switchInstance's own order: resolve the EFFECTIVE pin first (a setting the registry
    // no longer holds pins nothing), then report both warnings off that one value.
    const pin = inst.effectivePin(swReg, setting);
    let stored = "", persisted = true;
    try {
      if (!storageOk) throw new Error("storage is locked");
      stored = "work-vm";
    } catch (_) { persisted = false; }
    const plan = inst.planSwitchPersistence("work-vm", persisted, pin);
    const active = inst.resolveActive({
      registry: swReg, setting, workspaceValue: plan.override || stored,
    });
    return {
      message: plan.message, override: plan.override, pinned: plan.pinned,
      // the second warning switchInstance shows, driven by the SAME decision
      pinWarning: pin && pin !== "work-vm" ? pin : null,
      active: active.instance.name,
    };
  };
  const okSwitch = await switchWindow(true);
  eq("persist ok: the window is on the instance it switched to", okSwitch.active, "work-vm");
  eq("persist ok: no override is installed", okSwitch.override, "");
  eq("persist ok: nothing is reported", okSwitch.message, null);
  const failedSwitch = await switchWindow(false);
  eq("persist failed: the window is STILL on the instance it switched to", failedSwitch.active, "work-vm");
  eq("persist failed: ...held by an explicit window-local override", failedSwitch.override, "work-vm");
  ok("persist failed: ...and the message names that instance and says it won't persist",
    typeof failedSwitch.message === "string" && failedSwitch.message.includes('"work-vm"') &&
    failedSwitch.message.includes("couldn't be saved") && failedSwitch.message.includes("reloads"));
  // The bug the override fixes: with nothing stored and no override, the window resolves
  // the PREVIOUS instance while the user is told the switch took effect.
  eq("persist failed: without the override the window would fall back to the old instance",
    inst.resolveActive({ registry: swReg, setting: "", workspaceValue: "" }).instance.name, "agent-vm");
  // The construct.instance pin still outranks the override, exactly as it outranks
  // workspaceState — the override sits at the same precedence level, not above it.
  eq("persist failed: the global pin still wins over the override",
    inst.resolveActive({ registry: swReg, setting: "agent-vm", workspaceValue: failedSwitch.override }).instance.name,
    "agent-vm");
  deepEq("persist: a blank/absent name never becomes an override",
    inst.planSwitchPersistence("", false).override, "");
  // ...and with construct.instance PINNED to another instance the window does not move
  // at all, so the failure must not be reported as a switch that took effect. (The two
  // warnings the user sees — this one and the pin notice — then agree with each other
  // AND with the active target.)
  const pinnedFail = await switchWindow(false, "agent-vm");
  eq("persist failed + pinned: the pin is what the window actually uses", pinnedFail.active, "agent-vm");
  ok("persist failed + pinned: the message does NOT claim the window switched",
    typeof pinnedFail.message === "string" && !/Switched to/.test(pinnedFail.message) &&
    pinnedFail.message.includes('"work-vm"') && pinnedFail.message.includes("couldn't be saved"));
  eq("persist failed + pinned: ...and it is flagged as pinned", pinnedFail.pinned, true);
  eq("persist failed + pinned: the override is still installed, for when the pin is cleared",
    inst.resolveActive({ registry: swReg, setting: "", workspaceValue: pinnedFail.override }).instance.name,
    "work-vm");
  const pinnedOk = await switchWindow(true, "agent-vm");
  eq("persist ok + pinned: a successful write reports nothing (the pin notice stands alone)",
    pinnedOk.message, null);
  eq("persist ok + pinned: ...and the pin still wins", pinnedOk.active, "agent-vm");
  eq("persist: a pin naming the SAME instance is not a pin at all",
    inst.planSwitchPersistence("work-vm", false, "work-vm").pinned, false);
  // ...and a STALE `construct.instance` pins nothing: resolveActive skips a name the
  // registry no longer holds, so the window really does move. Reporting it as a pin made
  // BOTH warnings contradict the active target.
  eq("pin: a setting the registry holds is the pin", inst.effectivePin(swReg, "agent-vm"), "agent-vm");
  eq("pin: ...with surrounding whitespace trimmed", inst.effectivePin(swReg, "  agent-vm  "), "agent-vm");
  eq("pin: a setting naming a removed instance pins nothing", inst.effectivePin(swReg, "removed-vm"), "");
  eq("pin: an unset setting pins nothing", inst.effectivePin(swReg, ""), "");
  eq("pin: membership is an OWN-property test, so \"constructor\" is not a pin",
    inst.effectivePin(swReg, "constructor"), "");
  const staleSetting = await switchWindow(false, "removed-vm");
  eq("persist failed + STALE pin: the window is on the instance it switched to",
    staleSetting.active, "work-vm");
  eq("persist failed + STALE pin: ...so it is not treated as pinned", staleSetting.pinned, false);
  ok("persist failed + STALE pin: ...the message says the switch took effect but won't persist",
    /Switched to "work-vm" for this window/.test(staleSetting.message) &&
    staleSetting.message.includes("reloads"));
  eq("persist failed + STALE pin: ...and no pin warning contradicts it", staleSetting.pinWarning, null);
  // The control: a LIVE pin still produces both the non-switch wording and the warning.
  eq("persist failed + live pin: the pin warning names the instance in use",
    (await switchWindow(false, "agent-vm")).pinWarning, "agent-vm");
}

asyncTests().then(() => {
  try { fs.rmSync(tmpRoot, { recursive: true, force: true }); } catch (_) {}
  console.log(`\ninstance-registry unit tests — ${pass}/${pass + fail} passed`);
  if (fail) process.exit(1);
}, (e) => {
  console.log("  FAIL  async tests threw: " + (e && e.stack ? e.stack : e));
  process.exit(1);
});
