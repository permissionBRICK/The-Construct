"use strict";
// Plain-node unit tests for the extension-side driver dispatch (src/drivers/) and
// vmpower's instance-aware facade. Nothing here spawns a real powershell: the
// hyperv-local driver is exercised through the injected fake spawn (_spawn/_platform
// seams), and the unknown-backend driver never spawns at all.
// Run: node drivers.test.js
const { EventEmitter } = require("events");
const drivers = require("../src/drivers");
const instances = require("../src/instances");
const hypervLocal = require("../src/drivers/hyperv-local");
const hypervRemote = require("../src/drivers/hyperv-remote");
const vmpower = require("../src/vmpower");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}

// Same fake as vmpower.test.js: scripted stdout + close on the next tick, and it
// records the argv it was handed.
function fakeSpawn(behavior, sink) {
  return function (file, args) {
    if (sink) { sink.called = true; sink.file = file; sink.args = args; }
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.kill = () => { child.killed = true; };
    setImmediate(() => {
      if (behavior.error) { child.emit("error", new Error("spawn boom")); return; }
      if (behavior.data != null) child.stdout.emit("data", Buffer.from(behavior.data));
      if (!behavior.neverClose) child.emit("close", behavior.closeCode == null ? 0 : behavior.closeCode);
    });
    return child;
  };
}
const decodeLast = (args) => Buffer.from(args[args.length - 1], "base64").toString("utf16le");

// The normalized instance objects the registry hands around (contract §4.3).
const DEFAULT_INSTANCE = {
  name: "agent-vm", backend: "hyperv-local", vmName: "Agent-VM",
  vmHost: "agent-vm.mshome.net", sshPort: 22, hostAlias: "agent-vm",
  keyName: "agent_vm_ed25519", configBranch: "vm", scriptsDir: null,
};
// CANONICAL for a hyperv-local instance named "work-vm": the alias is the BARE
// instance name. The hardened registry reader REFUSES the older "construct-work-vm"
// spelling for this backend, so a fixture using it could not reach the driver through
// production parsing at all (extension/test/instances.test.js, "canonical local
// identity") -- it has to be what the reader really hands the driver.
const WORK_INSTANCE = {
  name: "work-vm", backend: "hyperv-local", vmName: "Work-VM",
  vmHost: "work-vm.mshome.net", sshPort: 22, hostAlias: "work-vm",
  keyName: "construct_work-vm_ed25519", configBranch: "vm-work-vm", scriptsDir: null,
};

// The fixture is pinned to PRODUCTION PARSING: every field it states must be what the
// registry reader derives for this instance, and the reader must accept it as a
// canonical hyperv-local identity -- an entry it refuses (e.g. the old
// "construct-work-vm" alias) can never reach a driver at all.
const READER_WORK = instances.deriveDefaults("work-vm", { backend: "hyperv-local", vmName: "Work-VM" });
ok("fixture: WORK_INSTANCE is what the registry reader produces",
  Object.keys(WORK_INSTANCE).every((k) => JSON.stringify(READER_WORK[k]) === JSON.stringify(WORK_INSTANCE[k])),
  JSON.stringify(READER_WORK));
ok("fixture: ...and the reader accepts that identity for hyperv-local",
  instances.localIdentityProblems(READER_WORK).length === 0);

// ── getDriver dispatch ───────────────────────────────────────────────────────
ok("dispatch: 'hyperv-local' -> the local Hyper-V driver", drivers.getDriver("hyperv-local") === hypervLocal);
ok("dispatch: undefined backend -> the default (local) driver", drivers.getDriver(undefined) === hypervLocal);
ok("dispatch: null/empty backend -> the default (local) driver",
  drivers.getDriver(null) === hypervLocal && drivers.getDriver("") === hypervLocal);
ok("dispatch: case/whitespace tolerant", drivers.getDriver("  HyperV-Local  ") === hypervLocal);
ok("dispatch: DEFAULT_BACKEND is hyperv-local (the zero-change path)", drivers.DEFAULT_BACKEND === "hyperv-local");
ok("dispatch: listBackends reports what this build implements",
  JSON.stringify(drivers.listBackends()) === JSON.stringify(["hyperv-local", "hyperv-remote"]));
ok("dispatch: 'hyperv-remote' -> the remote driver", drivers.getDriver("hyperv-remote") === hypervRemote);

// ── capabilities ─────────────────────────────────────────────────────────────
const caps = drivers.getDriver("hyperv-local").capabilities;
ok("caps: local Hyper-V has checkpoints", caps.checkpoints === true);
ok("caps: local Hyper-V console is vmconnect", caps.console === "vmconnect");
ok("caps: local Hyper-V can suspend (Save-VM)", caps.suspend === true);
ok("caps: the driver names its backend", hypervLocal.backend === "hyperv-local");

// ── the remote driver's capabilities ─────────────────────────────────────────
// hostLifecycle is TRUE since B7 (Auto-Install.ps1 gained -Backend hyperv-remote), and
// checkpoints stays FALSE — the two are separate questions, see the gate below.
const rcaps = hypervRemote.capabilities;
ok("caps: hyperv-remote has NO checkpoints", rcaps.checkpoints === false);
ok("caps: hyperv-remote has no console", rcaps.console === "none");
ok("caps: hyperv-remote can suspend (the idle policy saves it)", rcaps.suspend === true);
ok("caps: hyperv-remote declares hostLifecycle (Auto-Install's remote path)", rcaps.hostLifecycle === true);
ok("caps: the remote driver names its backend", hypervRemote.backend === "hyperv-remote");

// ── unknown backend degrades, never throws ───────────────────────────────────
const unk = drivers.getDriver("proxmox");   // no driver in this build
ok("unknown: getDriver doesn't throw and keeps the requested name",
  !!unk && unk.backend === "proxmox" && unk.unknown === true);
ok("unknown: no capabilities are claimed",
  unk.capabilities.checkpoints === false && unk.capabilities.console === "none" && unk.capabilities.suspend === false);

// ── lifecycle capability gate ────────────────────────────────────────────────
// The host PowerShell scripts drive the LOCAL Hyper-V, so the actions that create,
// delete or reconfigure a VM are refused for every other backend — otherwise a
// Reinstall of a remote instance would delete a LOCAL VM of the same name. The gate
// reads a declared capability, so a future remote driver enables the actions by
// declaring `hostLifecycle`, not by editing lifecycle.js.
ok("caps: local Hyper-V declares hostLifecycle (the host scripts manage it)", caps.hostLifecycle === true);
ok("caps: an unknown backend claims no hostLifecycle", unk.capabilities.hostLifecycle === false);
ok("gate: the hypervisor actions are exactly reinstall/redownload/setCheckpoints/setResources",
  JSON.stringify(drivers.HYPERVISOR_ACTIONS) === JSON.stringify(["reinstall", "redownload", "setCheckpoints", "setResources"]));
for (const action of drivers.HYPERVISOR_ACTIONS) {
  ok(`gate: hyperv-local may ${action}`, drivers.lifecycleSupport("hyperv-local", action).ok === true);
  const denied = drivers.lifecycleSupport("proxmox", action);
  ok(`gate: an unknown backend may NOT ${action}`, denied.ok === false);
  ok(`gate: ...and says why (${action})`, /local Hyper-V/i.test(denied.reason));
}
// hyperv-remote: rebuilds YES (they route through Auto-Install's remote path),
// checkpoints NO (the backend has none) — and the refusal says which of the two it is.
for (const action of ["reinstall", "redownload"]) {
  ok(`gate: hyperv-remote MAY ${action}`, drivers.lifecycleSupport("hyperv-remote", action).ok === true);
}
const noChk = drivers.lifecycleSupport("hyperv-remote", "setCheckpoints");
ok("gate: hyperv-remote may NOT setCheckpoints", noChk.ok === false);
ok("gate: ...and blames the missing capability, not the missing driver",
  /no checkpoints/i.test(noChk.reason) && !/remote driver/i.test(noChk.reason));
ok("gate: ACTION_CAPABILITY maps setCheckpoints onto the checkpoints capability",
  drivers.ACTION_CAPABILITY.setCheckpoints === "checkpoints");
// setResources (Set-AgentVmResources.ps1): the local driver resizes its own VMs in place;
// a remote primary is resized by its host service, so the script must never touch it.
ok("caps: local Hyper-V declares resources (in-place RAM/vCPU resize)", caps.resources === true);
ok("caps: hyperv-remote declares NO resources (the service resizes its VMs)", rcaps.resources === false);
ok("caps: an unknown backend claims no resources", unk.capabilities.resources === false);
ok("gate: ACTION_CAPABILITY maps setResources onto the resources capability",
  drivers.ACTION_CAPABILITY.setResources === "resources");
const noRes = drivers.lifecycleSupport("hyperv-remote", "setResources");
ok("gate: hyperv-remote may NOT setResources", noRes.ok === false);
ok("gate: ...and points at the host service, not at a missing driver",
  /host service/i.test(noRes.reason) && !/remote driver/i.test(noRes.reason));
for (const action of ["reprovision", "exportConfig"]) {
  ok(`gate: ${action} is SSH-only, so every backend may run it`,
    drivers.lifecycleSupport("hyperv-local", action).ok === true &&
    drivers.lifecycleSupport("hyperv-remote", action).ok === true &&
    drivers.lifecycleSupport("proxmox", action).ok === true);
  ok(`gate: ${action} is not a hypervisor action`, drivers.isHypervisorAction(action) === false);
}
ok("gate: a missing/empty backend is the default (local) one — the zero-change path",
  drivers.lifecycleSupport(undefined, "reinstall").ok === true &&
  drivers.lifecycleSupport("", "reinstall").ok === true &&
  drivers.lifecycleSupport(null, "reinstall").ok === true);
const logged = [];
ok("unknown: startVm declines and logs a reason",
  unk.startVm(WORK_INSTANCE, { _log: (m) => logged.push(m) }) === false &&
  logged.length === 1 && /proxmox/.test(logged[0]));

// ── vmpower.resolveTarget (the instance/vmName precedence) ───────────────────
ok("target: no opts -> the default instance",
  vmpower.resolveTarget({}).instance.vmName === "Agent-VM" && vmpower.resolveTarget({}).backend === "hyperv-local");
ok("target: undefined opts -> the default instance", vmpower.resolveTarget(undefined).instance.vmName === "Agent-VM");
ok("target: an instance supplies vmName + backend",
  vmpower.resolveTarget({ instance: WORK_INSTANCE }).instance.vmName === "Work-VM");
ok("target: an instance without a backend still lands on the default driver",
  vmpower.resolveTarget({ instance: { vmName: "X" } }).backend === "hyperv-local");
ok("target: an explicit opts.vmName still wins (legacy callers)",
  vmpower.resolveTarget({ instance: WORK_INSTANCE, vmName: "Other-VM" }).instance.vmName === "Other-VM");
ok("target: the instance object is copied, not mutated",
  vmpower.resolveTarget({ instance: WORK_INSTANCE, vmName: "Other-VM" }).instance !== WORK_INSTANCE &&
  WORK_INSTANCE.vmName === "Work-VM");
ok("target: other instance fields survive the copy",
  vmpower.resolveTarget({ instance: WORK_INSTANCE }).instance.hostAlias === "work-vm");
ok("facade: driverFor resolves the backend's driver", vmpower.driverFor({ instance: DEFAULT_INSTANCE }) === hypervLocal);
ok("facade: getDriver is re-exported", vmpower.getDriver === drivers.getDriver);

(async () => {
  // ── queries dispatch to the driver with the instance's VM name ─────────────
  const s1 = {};
  const st1 = await vmpower.queryVmState({
    instance: WORK_INSTANCE, _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, s1),
  });
  ok("query: an instance probes ITS vm name",
    st1 === "running" && decodeLast(s1.args).includes("Get-VM -Name 'Work-VM'"));

  const s2 = {};
  const st2 = await vmpower.queryVmState({ _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Off\n" }, s2) });
  ok("query: no instance probes the DEFAULT VM name (zero-change path)",
    st2 === "off" && decodeLast(s2.args).includes("Get-VM -Name 'Agent-VM'"));

  const s3 = {};
  await vmpower.queryAutoCheckpoints({
    instance: WORK_INSTANCE, _platform: "win32", _spawn: fakeSpawn({ data: "VMAUTOCHK=False\n" }, s3),
  });
  ok("autochk: an instance probes ITS vm name", decodeLast(s3.args).includes("Get-VM -Name 'Work-VM'"));

  // A registry entry naming a backend this build has no driver for must not spawn
  // anything and must read back as "can't tell" — what the UI already degrades on.
  const s4 = {};
  const foreign = { ...WORK_INSTANCE, backend: "proxmox" };
  const st4 = await vmpower.queryVmState({ instance: foreign, _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, s4) });
  const ac4 = await vmpower.queryAutoCheckpoints({ instance: foreign, _platform: "win32", _spawn: fakeSpawn({ data: "VMAUTOCHK=True\n" }, s4) });
  ok("unknown backend: queries resolve 'unknown' without spawning",
    st4 === "unknown" && ac4 === "unknown" && !s4.called);
  ok("unknown backend: startVm returns false (logged, not thrown)",
    vmpower.startVm({ instance: foreign, _log: () => {} }) === false);

  // A REMOTE instance dispatches to the remote driver: no powershell is spawned for a
  // state probe (it is an HTTPS call), and an entry with no service.url can't be
  // reached at all -> "can't tell", never "absent".
  const s4b = {};
  const remoteNoSvc = { ...WORK_INSTANCE, backend: "hyperv-remote", service: null };
  const st4b = await vmpower.queryVmState({ instance: remoteNoSvc, _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, s4b) });
  const ac4b = await vmpower.queryAutoCheckpoints({ instance: remoteNoSvc, _platform: "win32", _spawn: fakeSpawn({}, s4b) });
  ok("remote backend: a serviceless entry reads 'unknown' and spawns nothing",
    st4b === "unknown" && !s4b.called);
  ok("remote backend: checkpoints are 'unsupported', not probed", ac4b === "unsupported");
  ok("remote backend: startVm without a service URL declines",
    vmpower.startVm({ instance: remoteNoSvc, _log: () => {} }) === false);

  // ── the driver's own entry points (instance-first signature) ───────────────
  const s5 = {};
  const st5 = await hypervLocal.queryVmState(WORK_INSTANCE, { _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Saved\n" }, s5) });
  ok("driver: queryVmState(instance, opts) maps Saved -> off (resumable)", st5 === "off");
  ok("driver: queryVmState(null) falls back to the default VM name",
    (await hypervLocal.queryVmState(null, { _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Off\n" }, {}) })) === "off");

  // Off-Windows: no spawn, 'unknown' — the driver can't run powershell there.
  const s6 = {};
  const off = await hypervLocal.queryVmState(DEFAULT_INSTANCE, { _platform: "linux", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, s6) });
  ok("driver: off-Windows resolves unknown without spawning", off === "unknown" && !s6.called);

  // ── the builders are the SAME functions vmpower re-exports ─────────────────
  ok("facade: the pure builders are re-exported identically",
    vmpower.buildStateProbeCommand === hypervLocal.buildStateProbeCommand &&
    vmpower.parseVmState === hypervLocal.parseVmState &&
    vmpower.buildStartCommand === hypervLocal.buildStartCommand &&
    vmpower.buildElevatedCommandLaunch === hypervLocal.buildElevatedCommandLaunch);
  ok("facade: VM_NAME still Agent-VM on both sides",
    vmpower.VM_NAME === "Agent-VM" && hypervLocal.VM_NAME === "Agent-VM");
  ok("driver: an instance's vmName reaches the Start-VM command",
    hypervLocal.buildStartCommand(WORK_INSTANCE.vmName).includes("Start-VM -Name 'Work-VM'"));

  // ── the remote driver's lazy features + children (host-administration contract §10.1) ──
  console.log("\n  -- hyperv-remote: features and children --");
  const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);
  ok("caps: hyperv-remote's static table says children: false", hypervRemote.capabilities.children === false);
  const REMOTE = { name: "work-vm", backend: "hyperv-remote", vmName: "work-vm", service: { url: "http://127.0.0.1:7999", auth: "token" } };
  const fakeFetch = (routes) => async (url, init) => {
    const path = url.replace(/^http:\/\/127\.0\.0\.1:7999/, "");
    const r = routes[path];
    if (!r) return { status: 404, text: '{"title":"Not found"}' };
    if (typeof r === "function") return r(init);
    return { status: 200, text: JSON.stringify(r) };
  };
  {
    hypervRemote.resetFeatureCache();
    let healthCalls = 0;
    const fetchImpl = fakeFetch({
      "/api/v1/health": () => { healthCalls++; return { status: 200, text: JSON.stringify({ status: "ok", apiFeatures: ["host-admin", "children"] }) }; },
      "/api/v1/vms/work-vm/children": [{ name: "work-vm-a1", kind: "child", state: "running" }],
      "/api/v1/vms/shared": [{ name: "WORK-VM-A1", kind: "child", shared: true }, { name: "other-user-guest", owner: "bob", parent: "bob-vm", kind: "child", shared: true, allowedActions: ["inspect", "shutdown"] }],
    });
    const opts = { auth: { kind: "token", token: "t" }, fetchImpl, now: () => 1000 };
    const caps = await hypervRemote.capabilitiesFor(REMOTE, opts);
    ok("features: children resolves to true when /health lists it", caps.children === true);
    ok("features: ...and the rest of the table is unchanged", caps.checkpoints === false && caps.hostLifecycle === true);
    const kids = await hypervRemote.queryChildren(REMOTE, opts);
    ok("children: own and shared guests appear together without duplicate names", kids.supported === true && kids.items.length === 2 && kids.items[0].name === "work-vm-a1");
    ok("children: another user's shared guest retains its ownership and allowed actions", kids.items[1].owner === "bob" && kids.items[1].shared && !kids.items[1].allowedActions.includes("delete"));
    eq("features: the probe is cached per host (one /health for two questions)", healthCalls, 1);
    await hypervRemote.queryFeatures(REMOTE, { ...opts, now: () => 1000 + hypervRemote.FEATURE_TTL_MS + 1 });
    eq("features: ...and re-asked after the TTL", healthCalls, 2);
  }
  {
    hypervRemote.resetFeatureCache();
    const fetchImpl = fakeFetch({
      "/api/v1/health": { status: "ok", apiFeatures: ["children"] },
      "/api/v1/vms/work-vm/children": [],
      "/api/v1/vms/shared": [{ name: "shared-only", parent: "other-parent", kind: "child", shared: true }],
    });
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("children: shared guests are visible when this primary has no children", kids.supported && kids.items.length === 1 && kids.items[0].name === "shared-only");
  }
  {
    hypervRemote.resetFeatureCache();
    const fetchImpl = fakeFetch({
      "/api/v1/health": { status: "ok", apiFeatures: ["children"] },
      "/api/v1/vms/work-vm/children": [{ name: "own-child" }],
      "/api/v1/vms/shared": () => ({ status: 503, text: '{"title":"shared inventory unavailable"}' }),
    });
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("children: failure to load shared guests is reported rather than silently showing an incomplete list", kids.supported && kids.items === null && /shared inventory unavailable/.test(kids.problem));
  }
  {
    hypervRemote.resetFeatureCache();
    const fetchImpl = fakeFetch({ "/api/v1/vms/work-vm/children": [] });   // no /health at all: an OLD service
    const opts = { auth: { kind: "token", token: "t" }, fetchImpl };
    const probe = await hypervRemote.queryFeatures(REMOTE, opts);
    ok("features: an old service (404 on /health) is reported old, not failed", probe.old === true && probe.features === null);
    const kids = await hypervRemote.queryChildren(REMOTE, opts);
    ok("children: an old service is unsupported (the card hides) and its children route is never asked",
      kids.supported === false && kids.items.length === 0);
  }
  {
    hypervRemote.resetFeatureCache();
    const fetchImpl = fakeFetch({ "/api/v1/health": { status: "ok", apiFeatures: ["host-admin"] } });
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("children: a host-admin-only service (stage 1) is unsupported too", kids.supported === false);
  }
  {
    hypervRemote.resetFeatureCache();
    const fetchImpl = fakeFetch({
      "/api/v1/health": { status: "ok", apiFeatures: ["host-admin", "children"] },
      "/api/v1/vms/work-vm/children": () => ({ status: 500, text: '{"title":"boom"}' }),
      "/api/v1/vms/shared": [],
    });
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("children: a failed read on a supporting host is supported with items:null and a problem (not 'no children')",
      kids.supported === true && kids.items === null && /boom/.test(kids.problem) && kids.status === 500);
  }
  {
    hypervRemote.resetFeatureCache();
    let calls = 0;
    const fetchImpl = async () => { calls++; throw new Error("ECONNREFUSED"); };
    const first = await hypervRemote.queryFeatures(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    const second = await hypervRemote.queryFeatures(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("features: a transport failure is not cached (asked again)", first.features === null && !first.old && calls === 2 && second.features === null);
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "t" }, fetchImpl });
    ok("children: unreachable is unsupported (hidden), never a fabricated empty list", kids.supported === false);
  }
  {
    hypervRemote.resetFeatureCache();
    const kids = await hypervRemote.queryChildren(REMOTE, { auth: { kind: "token", token: "" }, fetchImpl: async () => ({ status: 200, text: "{}" }) });
    ok("children: a missing token is a problem, not a Negotiate fallback", kids.supported === false && /API token/.test(kids.problem));
  }

  console.log(`\n  drivers unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
