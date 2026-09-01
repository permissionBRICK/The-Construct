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
  JSON.stringify(drivers.listBackends()) === JSON.stringify(["hyperv-local"]));

// ── capabilities ─────────────────────────────────────────────────────────────
const caps = drivers.getDriver("hyperv-local").capabilities;
ok("caps: local Hyper-V has checkpoints", caps.checkpoints === true);
ok("caps: local Hyper-V console is vmconnect", caps.console === "vmconnect");
ok("caps: local Hyper-V can suspend (Save-VM)", caps.suspend === true);
ok("caps: the driver names its backend", hypervLocal.backend === "hyperv-local");

// ── unknown backend degrades, never throws ───────────────────────────────────
const unk = drivers.getDriver("hyperv-remote");   // not implemented in this build
ok("unknown: getDriver doesn't throw and keeps the requested name",
  !!unk && unk.backend === "hyperv-remote" && unk.unknown === true);
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
ok("gate: the hypervisor actions are exactly reinstall/redownload/setCheckpoints",
  JSON.stringify(drivers.HYPERVISOR_ACTIONS) === JSON.stringify(["reinstall", "redownload", "setCheckpoints"]));
for (const action of drivers.HYPERVISOR_ACTIONS) {
  ok(`gate: hyperv-local may ${action}`, drivers.lifecycleSupport("hyperv-local", action).ok === true);
  const denied = drivers.lifecycleSupport("hyperv-remote", action);
  ok(`gate: hyperv-remote may NOT ${action}`, denied.ok === false);
  ok(`gate: ...and says why (${action})`, /remote driver/i.test(denied.reason));
  ok(`gate: an unknown backend may NOT ${action}`, drivers.lifecycleSupport("proxmox", action).ok === false);
}
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
  logged.length === 1 && /hyperv-remote/.test(logged[0]));

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
  const remote = { ...WORK_INSTANCE, backend: "hyperv-remote" };
  const st4 = await vmpower.queryVmState({ instance: remote, _platform: "win32", _spawn: fakeSpawn({ data: "VMSTATE=Running\n" }, s4) });
  const ac4 = await vmpower.queryAutoCheckpoints({ instance: remote, _platform: "win32", _spawn: fakeSpawn({ data: "VMAUTOCHK=True\n" }, s4) });
  ok("unknown backend: queries resolve 'unknown' without spawning",
    st4 === "unknown" && ac4 === "unknown" && !s4.called);
  ok("unknown backend: startVm returns false (logged, not thrown)",
    vmpower.startVm({ instance: remote, _log: () => {} }) === false);

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

  console.log(`\n  drivers unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})();
