"use strict";
// Plain-node unit tests for src/hostadmin.js — the pure half of the host-administration
// module (host-administration contract §10). Nothing here touches a socket: the model is
// driven against a FAKE client whose answers and refusals are scripted per route.
// Run: node hostadmin.test.js
const ha = require("../src/hostadmin");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, got, want) => ok(name, got === want, `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);
const deep = (name, got, want) => ok(name, JSON.stringify(got) === JSON.stringify(want), `got ${JSON.stringify(got)} want ${JSON.stringify(want)}`);

/** An error the way src/remotehost.js throws them. */
function apiErr(status, body, message) {
  const e = new Error(message || `HTTP ${status}`);
  e.status = status;
  e.body = body == null ? null : body;
  e.code = body && typeof body === "object" && typeof body.code === "string" ? body.code : "";
  return e;
}

const HEALTH_FULL = { status: "ok", schemaVersion: 7, apiFeatures: ["host-admin", "children", "media", "console", "updates", "network"] };
const ME_ADMIN = { name: "DOMAIN\\alice", known: true, role: "admin", enabled: true, maxVms: 2, apiFeatures: HEALTH_FULL.apiFeatures };
const ME_USER = { ...ME_ADMIN, name: "DOMAIN\\bob", role: "user" };

/**
 * A scripted client: `answers[method name]` is a value, a function of the args, or an
 * Error to throw. Every call is recorded.
 */
function fakeClient(answers = {}) {
  const calls = [];
  const c = { host: "buildbox.example.local", calls };
  const methods = [
    "health", "whoami", "hostStatus", "hostCapacity", "hostConfig", "putHostConfig", "hostCapabilities", "isoCatalog",
    "users", "getUser", "createUser", "updateUser", "deleteUser", "userAllowance", "putUserAllowance", "userTokens", "issueUserToken", "revokeUserToken",
    "vms", "sharedVms", "children", "lifecycle", "deleteVm", "getJob", "overrides", "putOverrides", "deleteOverrides",
    "rotateVmToken", "revokeVmToken", "media", "deleteMedia", "mediaCleanup", "jobs", "cancelJob", "audit",
    "updatesStatus", "updatesCheck", "updatesStage", "updatesApply", "updatesCancel", "updatesResolve",
  ];
  for (const m of methods) {
    c[m] = async (...args) => {
      calls.push({ method: m, args });
      const a = answers[m];
      if (a instanceof Error) throw a;
      if (typeof a === "function") return a(...args);
      if (a === undefined) return m === "users" || m === "vms" || m === "jobs" || m === "audit" || m === "media" || m === "children" || m === "userTokens" ? [] : {};
      return a;
    };
  }
  return c;
}

(async () => {
  console.log("\n=== feature detection ===");
  deep("features: every flag false without apiFeatures", ha.featureSet({}), { hostAdmin: false, children: false, media: false, console: false, updates: false, network: false });
  deep("features: the full list", ha.featureSet(HEALTH_FULL), { hostAdmin: true, children: true, media: true, console: true, updates: true, network: true });
  deep("features: stage-1 service advertises host-admin only", ha.featureSet({ apiFeatures: ["host-admin"] }), { hostAdmin: true, children: false, media: false, console: false, updates: false, network: false });
  ok("maintenance: a 503 maintenance error is recognised", ha.isMaintenanceError(apiErr(503, { code: "maintenance", phase: "draining" })));
  ok("maintenance: a 503 without a body is treated as maintenance", ha.isMaintenanceError(apiErr(503, null)));
  ok("maintenance: a 500 is not", !ha.isMaintenanceError(apiErr(500, { code: "maintenance" })));
  deep("maintenance: from a health body", ha.maintenanceOf({ status: "maintenance", maintenance: { phase: "handedOff", retryAfterSeconds: 10, updateId: "u1" } }),
    { phase: "handedOff", retryAfterSeconds: 10, updateId: "u1" });

  console.log("\n=== §10.3 state table ===");
  const H = "buildbox.example.local";
  eq("state: a local backend renders nothing", ha.classifyHost({ backend: "hyperv-local", host: H }).mode, "local");
  let s = ha.classifyHost({ backend: "hyperv-remote", host: H, healthError: apiErr(0, null, "ECONNREFUSED") });
  eq("state: /health status 0 is Unavailable", s.mode, "unavailable");
  ok("state: ...retryable, naming the host", s.retryable && /Cannot reach buildbox/.test(s.message));
  s = ha.classifyHost({ host: H, healthError: apiErr(404, { title: "Not found" }) });
  eq("state: /health 404 is Old service", s.mode, "old-service");
  ok("state: ...telling the admin to update on the host, not from here", /Install-ConstructHost\.ps1/.test(s.message) && /no update can be driven from here/.test(s.message));
  s = ha.classifyHost({ host: H, health: { status: "ok", apiFeatures: [] }, whoami: ME_ADMIN });
  eq("state: a /health without host-admin is Old service too", s.mode, "old-service");
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoamiError: apiErr(401, null) });
  eq("state: /whoami 401 is Sign in again", s.mode, "sign-in");
  ok("state: ...pointing at the enrolment flow", /Add Remote Host/.test(s.message));
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoamiError: apiErr(403, null) });
  eq("state: /whoami 403 is Denied", s.mode, "denied");
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoami: { ...ME_USER, known: false } });
  eq("state: known=false is Denied", s.mode, "denied");
  ok("state: ...naming the identity and the host", /DOMAIN\\bob/.test(s.message) && /buildbox/.test(s.message));
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoami: { ...ME_ADMIN, enabled: false } });
  eq("state: enabled=false is Denied even for an admin", s.mode, "denied");
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoami: ME_USER });
  eq("state: role=user is the ordinary-user state", s.mode, "user");
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoami: ME_ADMIN });
  eq("state: role=admin is the full module", s.mode, "admin");
  eq("state: ...with the identity", s.identity.name, "DOMAIN\\alice");
  ok("state: ...and the features", s.features.children === true && s.features.updates === true);
  s = ha.classifyHost({ host: H, health: { ...HEALTH_FULL, status: "maintenance", maintenance: { phase: "draining", retryAfterSeconds: 5 } }, whoami: ME_ADMIN });
  eq("state: maintenance in /health keeps the admin mode", s.mode, "admin");
  eq("state: ...and carries the phase", s.maintenance.phase, "draining");
  s = ha.classifyHost({ host: H, healthError: apiErr(503, { code: "maintenance", phase: "maintenance", retryAfterSeconds: 10 }), whoamiError: apiErr(503, { code: "maintenance", phase: "maintenance" }) });
  eq("state: a 503 on both probes is Unavailable with the maintenance banner", s.mode, "unavailable");
  eq("state: ...phase kept", s.maintenance.phase, "maintenance");
  ok("state: ...message says reconnecting", /reconnecting/.test(s.message));
  s = ha.classifyHost({ host: H, health: HEALTH_FULL, whoami: { name: "x", known: true, role: "Admin" } });
  eq("state: role is case-insensitive", s.mode, "admin");

  console.log("\n=== resolveHostState (probe order) ===");
  {
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN });
    const r = await ha.resolveHostState(c, { host: H, backend: "hyperv-remote" });
    eq("resolve: admin", r.mode, "admin");
    deep("resolve: health first, whoami second", c.calls.map((x) => x.method), ["health", "whoami"]);
  }
  {
    const c = fakeClient({ health: apiErr(404, null), whoami: ME_ADMIN });
    const r = await ha.resolveHostState(c, { host: H });
    eq("resolve: an old service is not asked whoami", c.calls.length, 1);
    eq("resolve: ...and is old-service", r.mode, "old-service");
  }
  {
    const c = fakeClient({ health: apiErr(0, null, "timeout") });
    const r = await ha.resolveHostState(c, { host: H });
    eq("resolve: unreachable stops at health", c.calls.length, 1);
    eq("resolve: ...unavailable", r.mode, "unavailable");
  }
  {
    const r = await ha.resolveHostState(null, { host: H, problem: "the API token is gone" });
    eq("resolve: no client is unavailable", r.mode, "unavailable");
    ok("resolve: ...with the reason", /token is gone/.test(r.message));
  }
  {
    const r = await ha.resolveHostState(fakeClient(), { host: H, backend: "hyperv-local" });
    eq("resolve: a local backend never probes", r.mode, "local");
  }

  console.log("\n=== refusals flip the state ===");
  const admin = { mode: "admin", message: "", maintenance: null, host: H };
  eq("refusal: a later 403 flips to user", ha.applyRefusal(admin, apiErr(403, null), H).mode, "user");
  eq("refusal: a later 401 flips to sign-in", ha.applyRefusal(admin, apiErr(401, null), H).mode, "sign-in");
  eq("refusal: a 503 maintenance keeps admin and sets the banner", ha.applyRefusal(admin, apiErr(503, { code: "maintenance", phase: "draining" }), H).mode, "admin");
  eq("refusal: ...phase", ha.applyRefusal(admin, apiErr(503, { code: "maintenance", phase: "draining" }), H).maintenance.phase, "draining");
  eq("refusal: a transport failure flips to unavailable", ha.applyRefusal(admin, apiErr(0, null, "reset"), H).mode, "unavailable");
  eq("refusal: a 409 changes nothing", ha.applyRefusal(admin, apiErr(409, { code: "name-taken" }), H).mode, "admin");

  console.log("\n=== tabs and polling ===");
  {
    const tabs = ha.tabsFor({ features: ha.featureSet({ apiFeatures: ["host-admin"] }) });
    eq("tabs: seven tabs", tabs.length, 7);
    ok("tabs: overview available with host-admin", tabs.find((t) => t.id === "overview").available);
    const m = tabs.find((t) => t.id === "maintenance");
    ok("tabs: maintenance needs the updates feature", !m.available && /not available on this host version/.test(m.reason));
    ok("tabs: the full set makes every tab available", ha.tabsFor({ features: ha.featureSet(HEALTH_FULL) }).every((t) => t.available));
  }
  eq("poll: 5 s while updating", ha.pollIntervalMs({ maintenance: { phase: "draining" } }), 5000);
  eq("poll: nothing otherwise", ha.pollIntervalMs({ maintenance: null }), null);
  eq("poll: live VMs every 10 s", ha.pollIntervalMs({ mode: "admin", activeTab: "vms" }), 10000);
  eq("poll: no refresh over editable users", ha.pollIntervalMs({ mode: "admin", activeTab: "users" }), null);

  console.log("\n=== formatting ===");
  eq("bytes: GiB", ha.formatBytes(8 * ha.GIB), "8.0 GiB");
  eq("bytes: large GiB rounds", ha.formatBytes(64 * ha.GIB), "64 GiB");
  eq("bytes: MiB", ha.formatBytes(512 * 1024 * 1024), "512 MiB");
  eq("bytes: null is a dash", ha.formatBytes(null), "—");
  eq("when: ISO -> UTC minute", ha.formatWhen("2026-09-07T10:12:33Z"), "2026-09-07 10:12 UTC");
  eq("when: absent", ha.formatWhen(null), "—");
  eq("duration: hours and minutes", ha.formatDuration(7500), "2h 5m");
  eq("duration: days", ha.formatDuration(3 * 86400 + 3600), "3d 1h");
  eq("pct: clamped", ha.pct(200, 100), 100);
  eq("pct: unknown whole is 0", ha.pct(5, null), 0);

  console.log("\n=== leases, guests, resources ===");
  const NOW = Date.parse("2026-09-07T10:00:00Z");
  eq("lease: active with time left", ha.leaseText({ state: "active", expiresAt: "2026-09-07T12:05:00Z" }, NOW), "expires in 2h 5m (2026-09-07 12:05 UTC)");
  eq("lease: active but due", ha.leaseText({ state: "active", expiresAt: "2026-09-07T09:00:00Z" }, NOW), "due since 2026-09-07 09:00 UTC");
  eq("lease: overdue says so with the last outcome", ha.leaseText({ state: "overdue", overdue: true, lastOutcome: "unavailable" }, NOW), "OVERDUE — unavailable (shutdown due; retried)");
  eq("lease: overdue flag wins over the state", ha.leaseText({ state: "active", overdue: true }, NOW).indexOf("OVERDUE"), 0);
  eq("lease: unlimited", ha.leaseText({ state: "unlimited" }, NOW), "no expiry");
  eq("lease: inactive", ha.leaseText({ state: "inactive" }, NOW), "not started");
  eq("lease: expired", ha.leaseText({ state: "expired", expiresAt: "2026-09-07T09:00:00Z" }, NOW), "expired at 2026-09-07 09:00 UTC");
  eq("lease: a primary has none", ha.leaseText(null, NOW), "");
  eq("guest: unknown values print unknown", ha.guestText({}), "Construct unknown · provisioned unknown · reinstalled unknown");
  ok("guest: known values print", /Construct abc1234 · provisioned 2026-09-01 08:00 UTC · reinstalled unknown/.test(ha.guestText({ constructCommit: "abc1234", provisionedAt: "2026-09-01T08:00:00Z" })));
  ok("guest: a failed attempt never overwrites the facts", /provisioned 2026-09-01 08:00 UTC.*last attempt failed/.test(ha.guestText({ provisionedAt: "2026-09-01T08:00:00Z", lastAttemptOutcome: "failed", lastAttemptAt: "2026-09-02T08:00:00Z" })));
  eq("resources: cpus, ram, disk", ha.resourcesText({ cpus: 4, ramMb: 8192, diskGb: 80 }), "4 vCPU · 8.0 GiB · 80 GB disk");
  eq("resources: unknown", ha.resourcesText(null), "—");
  eq("resources: migrated primary uses original allocation", ha.toVmRow({ name: "primary", cpu: 8, ramGb: 16, diskGb: 150 }, NOW).resources,
    "8 vCPU · 16 GiB · 150 GB disk");
  {
    const live = ha.resourceUsageView({ observedAt: new Date(NOW).toISOString(), cpuUsagePercent: 0,
      memoryAssignedBytes: 16 * 2 ** 30, memoryDemandBytes: 5 * 2 ** 30, diskFileBytes: 40 * 2 ** 30 }, NOW);
    eq("usage: idle CPU is measured zero", live.cpuPercent, 0);
    eq("usage: demand divided by assignment", live.ramPercent, 31);
    eq("usage: allocation isn't demand", live.ram, "5.0 GiB RAM demand / 16 GiB assigned");
    eq("usage: disk is explicitly host space", live.disk, "40 GiB disk on host");
    ok("usage: current sample is fresh", !live.stale);
    ok("usage: old sample is stale", ha.resourceUsageView({ observedAt: new Date(NOW - 31000).toISOString() }, NOW).stale);
    eq("usage: older server isn't zero", ha.resourceUsageView(null, NOW).cpuPercent, null);
    eq("usage: missing RAM isn't zero", ha.resourceUsageView(null, NOW).ramPercent, null);
    eq("usage: invalid CPU isn't shown", ha.resourceUsageView({ cpuUsagePercent: 101 }, NOW).cpu, "CPU usage unavailable");
  }
  eq("operation: kind, phase, initiator", ha.operationText({ jobId: "j", kind: "vm-shutdown", phase: "wait", initiator: "vm:work-vm" }), "vm-shutdown (wait) by vm:work-vm");

  console.log("\n=== VM rows ===");
  const VMS = [
    { name: "work-vm", owner: "alice", kind: "primary", state: "running", hardware: { cpus: 4, ramMb: 8192, diskGb: 80 }, children: ["work-vm-a1b2"], allowedActions: ["inspect", "shutdown", "delete", "overrides", "rotateToken"], tokenKind: "primary", guest: {}, reservations: { ramBytes: 8 * ha.GIB, cpus: 4, storageBytes: 80 * 1000 * 1000 * 1000 } },
    { name: "zeta-vm", owner: "bob", kind: "primary", state: "off", children: [] },
    { name: "work-vm-a1b2", owner: "alice", kind: "child", parent: "work-vm", sharing: "host", state: "running", lease: { state: "active", expiresAt: "2026-09-07T12:05:00Z" }, currentOperation: { jobId: "j9", kind: "vm-shutdown", phase: "wait" }, allowedActions: ["inspect", "shutdown", "delete", "bogus"] },
    { name: "orphan-child", owner: "carol", kind: "child", parent: "gone-vm", state: "saved" },
  ];
  {
    const rows = ha.toVmRows(VMS, NOW);
    deep("vms: children sit under their parent, primaries sorted, orphans last", rows.map((r) => r.name), ["work-vm", "work-vm-a1b2", "zeta-vm", "orphan-child"]);
    const child = rows[1];
    eq("vms: kind", child.kind, "child");
    eq("vms: parent", child.parent, "work-vm");
    eq("vms: sharing", child.sharing, "host");
    eq("vms: lease text", child.lease, "expires in 2h 5m (2026-09-07 12:05 UTC)");
    eq("vms: current operation", child.operation, "vm-shutdown (wait)");
    eq("vms: operation job id", child.operationJobId, "j9");
    deep("vms: unknown allowed actions are dropped", child.allowedActions, ["inspect", "shutdown", "delete"]);
    eq("vms: primary resources", rows[0].resources, "4 vCPU · 8.0 GiB · 80 GB disk");
    eq("vms: primary guest facts are honest", rows[0].guest, "Construct unknown · provisioned unknown · reinstalled unknown");
    eq("vms: reservations text", rows[0].reservations, "8.0 GiB RAM · 4 vCPU · 75 GiB storage");
    eq("vms: a primary without hardware", rows[2].resources, "—");
    deep("vms: children names", rows[0].children, ["work-vm-a1b2"]);
  }

  console.log("\n=== overview ===");
  {
    const status = {
      version: { commit: "deadbeef", packageVersion: "1.2.3", installedAt: "2026-09-01T00:00:00Z", source: "release" },
      health: { hypervisor: "ok", database: "ok", media: "missing-root", inventory: "complete" },
      capacityMode: "enforce",
      capacity: { epoch: 7, observedAt: "2026-09-07T09:59:00Z", complete: true,
        ram: { totalBytes: 32 * ha.GIB, headroomBytes: 4 * ha.GIB, reservedBytes: 16 * ha.GIB, unmanagedBytes: 2 * ha.GIB, physicalFreeBytes: 10 * ha.GIB, availableBytes: 10 * ha.GIB },
        cpu: { logical: 16, budget: null, active: 8, available: null },
        volumes: [{ root: "C:\\", totalBytes: 1000 * ha.GIB, freeBytes: 500 * ha.GIB, headroomBytes: 20 * ha.GIB, growthReservedBytes: 100 * ha.GIB, availableBytes: 380 * ha.GIB }] },
      maintenance: { phase: "open" },
      activeJobs: [{ id: "j1", kind: "child-create", vmName: "work-vm-a1b2", owner: "alice", initiator: "vm:work-vm", phase: "disk", created: "2026-09-07T09:58:00Z" }],
      leaseOverdueCount: 1, unmanagedVmCount: 2,
    };
    const ov = ha.toOverview(status);
    eq("overview: capacity mode badge", ov.capacityMode, "enforce");
    deep("overview: health problems listed", ov.problems, ["media missing-root"]);
    eq("overview: RAM bar pct = reserved+unmanaged+headroom of total", ov.capacity[0].pct, 69);
    ok("overview: RAM bar text", /10 GiB available of 32 GiB/.test(ov.capacity[0].text));
    eq("overview: CPU without a budget has no percentage", ov.capacity[1].pct, null);
    ok("overview: CPU text says no budget", /no budget/.test(ov.capacity[1].text));
    eq("overview: one volume bar", ov.capacity.length, 3);
    eq("overview: volume pct", ov.capacity[2].pct, 62);
    eq("overview: open maintenance is null", ov.maintenance, null);
    eq("overview: active jobs", ov.activeJobs.length, 1);
    eq("overview: overdue count", ov.leaseOverdueCount, 1);
    eq("overview: unmanaged count", ov.unmanagedVmCount, 2);
    eq("overview: version", ov.version.commit, "deadbeef");
    eq("overview: draining maintenance shows", ha.toOverview({ maintenance: { phase: "draining", since: "2026-09-07T09:00:00Z" } }).maintenance.phase, "draining");
    eq("overview: empty status does not throw", ha.toOverview(null).version.commit, "unknown");
    const hiddenRoot = "\\\\?\\Volume{a8247be4-28f0-4613-95f3-bb60e74a2876}\\";
    const storageBars = ha.toCapacityBars({ volumes: [
      { root: hiddenRoot, totalBytes: 500000000, growthReservedBytes: 0 },
      { root: "C:\\", totalBytes: 1000000000000 },
      { root: "C:\\VMs\\", totalBytes: 1000000000000 },
    ] });
    eq("overview: hidden unused partitions omitted", storageBars.length, 4);
    eq("overview: drive letter is readable", storageBars[2].label, "Storage C:\\");
    eq("overview: directory mount retained", storageBars[3].label, "Storage C:\\VMs\\");
    const usedHidden = ha.toCapacityBars({ volumes: [{ root: hiddenRoot, growthReservedBytes: 100 }] });
    eq("overview: unmounted volume with VM reservations retained", usedHidden.length, 3);
    eq("overview: unmounted volume has readable label", usedHidden[2].label, "Storage (unmounted volume)");
    eq("overview: CPU with a budget", ha.toCapacityBars({ cpu: { logical: 16, budget: 10, active: 5 } })[1].pct, 50);
  }

  console.log("\n=== users, media, jobs, audit, config, updates ===");
  {
    const u = ha.toUserRow({ name: "alice", role: "Admin", enabled: true, maxVms: 2, allowHostForwards: false, created: "2026-01-01T00:00:00Z",
      allowance: { allowChildCreation: null, maxRetainedChildren: 3, ramBudgetBytes: 16 * ha.GIB, maxChildLifetimeSeconds: 43200, allowSharing: false },
      effective: { maxPrimaries: 2, allowChildCreation: true, maxRetainedChildren: 3, cpuBudget: null, ramBudgetBytes: 16 * ha.GIB, storageBudgetBytes: null, maxChildLifetimeSeconds: 43200, allowNeverLifetime: true, allowSharing: false, allowHostForwards: false, usage: { primaries: 1, children: 2, cpus: 6, ramBytes: 12 * ha.GIB, storageBytes: 100 * ha.GIB } },
      vms: { primaries: 1, children: 2 }, tokens: 1 });
    eq("user: role lowercased", u.role, "admin");
    eq("user: allowance form: inherit is empty", u.allowance.allowChildCreation, "");
    eq("user: allowance form: number", u.allowance.maxRetainedChildren, "3");
    eq("user: allowance form: GiB", u.allowance.ramBudgetGiB, "16");
    eq("user: allowance form: lifetime text", u.allowance.maxChildLifetime, "12h");
    eq("user: allowance form: false", u.allowance.allowSharing, "false");
    ok("user: effective text", /2 primaries · 3 children · CPU no budget · RAM 16 GiB · storage no budget · lifetime ≤ 12h 0m · no sharing · no host forwards · in use: 1 primaries, 2 children, 6 vCPU, 12 GiB RAM, 100 GiB storage/.test(u.effective), u.effective);
    eq("user: primaries/children", u.primaries + "/" + u.children, "1/2");
  }
  {
    const m = ha.toMediaRow({ id: "m1", owner: "alice", name: "ubuntu.iso", role: "install", source: "url", state: "Ready", sizeBytes: 3 * ha.GIB, reservedBytes: 3 * ha.GIB, references: 2, created: "2026-09-01T00:00:00Z" });
    eq("media: state lowercased", m.state, "ready");
    eq("media: size", m.size, "3.0 GiB");
    ok("media: referenced items are not deletable from here", !m.deletable);
    ok("media: unreferenced items are", ha.toMediaRow({ id: "m2", references: 0 }).deletable);
    const cat = ha.toIsoCatalogView({ mode: "native", source: { path: "C:\\iso\\ubuntu.iso", sha256Configured: true, present: true, sizeBytes: 2 * ha.GIB }, current: { fileName: "construct-autoinstall-1.iso", sizeBytes: 2 * ha.GIB, builtAt: "2026-09-01T00:00:00Z" }, entries: [{ fileName: "construct-autoinstall-1.iso", isCurrent: true, sizeBytes: 1 }], lastBuild: { at: "2026-09-01T00:00:00Z", outcome: "succeeded" } });
    eq("catalog: current file", cat.current.fileName, "construct-autoinstall-1.iso");
    eq("catalog: entries", cat.entries.length, 1);
    eq("catalog: last build", cat.lastBuild.outcome, "succeeded");
    eq("catalog: empty is safe", ha.toIsoCatalogView(null).current, null);
  }
  {
    const j = ha.toJobRow({ id: "j1", kind: "child-delete", vmName: "work-vm-a1b2", owner: "alice", state: "Failed", phase: "vm", error: "boom", created: "2026-09-07T09:00:00Z" });
    eq("job: failed child-delete offers a retry by deleting again", j.retry && j.retry.action, "deleteVm");
    eq("job: ...naming the child", j.retry.name, "work-vm-a1b2");
    eq("job: ...as a child", j.retry.kind, "child");
    ok("job: terminal, not cancellable", j.terminal && !j.cancellable);
    const cas = ha.toJobRow({ id: "j2", kind: "parent-cascade-delete", vmName: "work-vm", state: "failed" });
    eq("job: a failed cascade retries the primary delete", cas.retry.kind, "primary");
    eq("job: a failed media-cleanup retries the cleanup", ha.toJobRow({ id: "j3", kind: "media-cleanup", state: "failed" }).retry.action, "mediaCleanup");
    eq("job: a failed create has no retry button", ha.toJobRow({ id: "j4", kind: "child-create", state: "failed", vmName: "x" }).retry, null);
    const run = ha.toJobRow({ id: "j5", kind: "vm-shutdown", state: "running", phase: "wait" });
    ok("job: a running job is cancellable and not terminal", run.cancellable && !run.terminal);
    eq("audit: row", ha.toAuditRow({ at: "2026-09-07T09:00:00Z", actor: "alice", action: "vm.lifecycle", target: "work-vm", detail: "action=shutdown" }).action, "vm.lifecycle");
  }
  {
    const cfg = ha.toConfigView({
      capacity: { value: { mode: "observe", storageHeadroomBytes: 21474836480 }, source: "stored", updatedAt: "2026-09-07T09:00:00Z" },
      lifecycle: { gracefulShutdownTimeoutSeconds: 300, leaseTickSeconds: 30, leaseRetrySeconds: 600, source: "default" },
    });
    eq("config: every §1.5 section is a row", cfg.length, 7);
    eq("config: a stored section keeps its source", cfg[0].source, "stored");
    eq("config: ...and its updatedAt for CAS", cfg[0].expectedUpdatedAt, "2026-09-07T09:00:00Z");
    ok("config: the JSON text is the value only", cfg[0].text.indexOf('"mode": "observe"') >= 0 && cfg[0].text.indexOf("updatedAt") < 0);
    const lc = cfg.find((c) => c.key === "lifecycle");
    ok("config: a flat section is accepted and stripped of its metadata", lc.text.indexOf("gracefulShutdownTimeoutSeconds") >= 0 && lc.text.indexOf("source") < 0);
    eq("config: ...source default", lc.source, "default");
    ok("config: an absent section is an empty object", cfg.find((c) => c.key === "updates").text === "{}");
    const caps = ha.toCapabilityRows({ backend: "hyperv-local", capabilities: { generations: [2], console: { screenshot: "supported", interactive: "unsupported" }, notes: ["LocalSystem execution unverified"] }, policy: { hostForwardsEnabled: true } });
    ok("capabilities: nested keys are flattened", caps.rows.some((r) => r.key === "console.interactive" && r.value === "unsupported"));
    ok("capabilities: arrays join", caps.rows.some((r) => r.key === "generations" && r.value === "2"));
    ok("capabilities: policy rows", caps.rows.some((r) => r.key === "policy.hostForwardsEnabled" && r.value === "true"));
    deep("capabilities: notes", caps.notes, ["LocalSystem execution unverified"]);
  }
  {
    const st = { installed: { commit: "1234567890abcdef", packageVersion: "1.0", installedAt: "2026-09-01T00:00:00Z", source: "release" }, current: { updateId: "u1", commit: "fedcba", state: "staged", phases: [{ name: "verify", at: "2026-09-07T09:00:00Z", outcome: "ok" }], started: "2026-09-07T09:00:00Z", blockingJobs: [] }, history: [] };
    const v = ha.toUpdateView(st);
    eq("updates: installed commit", v.installed.commit, "1234567890abcdef");
    eq("updates: current state", v.current.state, "staged");
    ok("updates: staged offers apply and cancel, not stage", v.actions.apply && v.actions.cancel && !v.actions.stage && !v.actions.resume);
    const a2 = ha.updateActionsFor({ current: { state: "interrupted" } });
    ok("updates: interrupted offers resume and resolve", a2.resume && a2.resolve && !a2.apply && !a2.stage);
    const a3 = ha.updateActionsFor({});
    ok("updates: stage needs no signing configuration", a3.stage);
    const a4 = ha.updateActionsFor({ current: { state: "succeeded" } });
    ok("updates: a finished update allows a new stage", a4.stage && !a4.cancel);
    ok("updates: recoveryFailed offers resolve", ha.updateActionsFor({ current: { state: "recoveryFailed" } }).resolve);
    ok("check: same commit is 'latest'", /is the latest release/.test(ha.checkResultText({ installed: { commit: "abc" }, latest: { commit: "abc", releaseTag: "v1" } })));
    ok("check: incompatible carries reasons", /NOT compatible: schema 9 > 7/.test(ha.checkResultText({ installed: { commit: "abc" }, latest: { commit: "def", releaseTag: "v2", compatible: false, reasons: ["schema 9 > 7"] } })));
    ok("check: no release", /No release found/.test(ha.checkResultText({ installed: { commit: "abc" } })));
  }

  console.log("\n=== forms ===");
  eq("lifetime: never", ha.parseLifetime("never").seconds, null);
  eq("lifetime: 12h", ha.parseLifetime("12h").seconds, 43200);
  eq("lifetime: 3d", ha.parseLifetime("3d").seconds, 259200);
  ok("lifetime: below 5m is refused", !ha.parseLifetime("4m").ok && /minimum/.test(ha.parseLifetime("4m").reason));
  ok("lifetime: garbage is refused", !ha.parseLifetime("soon").ok);
  ok("lifetime: empty is refused", !ha.parseLifetime("").ok);
  eq("lifetime text: 12h", ha.lifetimeTextOf(43200), "12h");
  eq("lifetime text: 90m", ha.lifetimeTextOf(5400), "90m");
  eq("lifetime text: 2d", ha.lifetimeTextOf(172800), "2d");
  {
    const p = ha.parseAllowanceForm({ allowChildCreation: "true", maxRetainedChildren: "2", cpuBudget: "", ramBudgetGiB: "16", storageBudgetGiB: "0.5", maxChildLifetime: "12h", allowNeverLifetime: "false", allowSharing: "" });
    ok("allowance: a valid form is ok", p.ok, JSON.stringify(p.problems));
    deep("allowance: the body", p.body, { allowChildCreation: true, maxRetainedChildren: 2, cpuBudget: null, ramBudgetBytes: 16 * ha.GIB, storageBudgetBytes: ha.GIB / 2, maxChildLifetimeSeconds: 43200, allowNeverLifetime: false, allowSharing: null });
    const bad = ha.parseAllowanceForm({ maxRetainedChildren: "-1", cpuBudget: "two", ramBudgetGiB: "x", maxChildLifetime: "never", allowSharing: "maybe" });
    ok("allowance: every problem is reported with its field", !bad.ok && bad.problems.map((x) => x.field).sort().join(",") === "allowSharing,cpuBudget,maxChildLifetime,maxRetainedChildren,ramBudgetGiB", JSON.stringify(bad.problems));
    ok("allowance: 'never' as a maximum is refused with a reason", bad.problems.find((x) => x.field === "maxChildLifetime").reason.indexOf("never") >= 0);
    const empty = ha.parseAllowanceForm({});
    ok("allowance: an empty form inherits everything", empty.ok && Object.values(empty.body).every((v) => v === null));
    const o = ha.parseOverridesForm({ allowChildCreation: "false", ramBudgetGiB: "nope", maxChildLifetime: "1h" });
    ok("overrides: only the restrict-only subset is in the body", Object.keys(o.body).sort().join(",") === "allowChildCreation,allowNeverLifetime,allowSharing,maxChildLifetimeSeconds,maxRetainedChildren");
    ok("overrides: problems outside the subset are dropped", o.ok);
    eq("overrides: lifetime", o.body.maxChildLifetimeSeconds, 3600);
  }
  {
    const u = ha.parseUserForm({ role: "Admin", enabled: "false", maxVms: "3", allowHostForwards: "" });
    deep("user form: only the set fields", u.body, { role: "admin", enabled: false, maxVms: 3 });
    ok("user form: nothing to change is a problem", !ha.parseUserForm({}).ok);
    ok("user form: a bad role is refused", !ha.parseUserForm({ role: "root" }).ok);
    const n = ha.parseNewUserForm({ name: "DOMAIN\\carol", role: "", maxVms: "1" });
    deep("new user: defaults to role user", n.body, { name: "DOMAIN\\carol", role: "user", maxVms: 1 });
    ok("new user: a name is required", !ha.parseNewUserForm({ role: "user" }).ok);
  }
  {
    ok("config: unknown section refused", !ha.parseConfigSection("secrets", "{}").ok);
    ok("config: bad JSON refused with the section as field", !ha.parseConfigSection("capacity", "{oops").ok && ha.parseConfigSection("capacity", "{oops").problems[0].field === "capacity");
    ok("config: an array refused", !ha.parseConfigSection("capacity", "[]").ok);
    const req = ha.buildConfigRequest([{ key: "capacity", text: '{"mode":"enforce"}', expectedUpdatedAt: "2026-09-07T09:00:00Z" }, { key: "network", text: '{"hostForwardsEnabled":false}' }]);
    ok("config request: ok", req.ok);
    deep("config request: sections replaced whole, CAS stamp added where known", req.body, { capacity: { mode: "enforce", expectedUpdatedAt: "2026-09-07T09:00:00Z" }, network: { hostForwardsEnabled: false } });
    ok("config request: nothing changed is a problem", !ha.buildConfigRequest([]).ok);
    deep("config problems: a 400 validation lands inline", ha.configProblemsOf(apiErr(400, { code: "validation", field: "capacity.ramHeadroomBytes", reason: "must be ≥ 0" })), [{ field: "capacity.ramHeadroomBytes", reason: "must be ≥ 0" }]);
    ok("config problems: a CAS conflict says to reload", /reload/.test(ha.configProblemsOf(apiErr(409, { code: "config-conflict" }))[0].reason));
  }

  console.log("\n=== minimal user view ===");
  {
    const rows = ha.childRows([
      { name: "work-vm-b2", state: "Running", sharing: "host", lease: { state: "active", expiresAt: "2026-09-07T12:05:00Z" }, allowedActions: ["inspect", "shutdown", "delete"] },
      { name: "work-vm-a1", state: "off", sharing: "private", lease: { state: "overdue", overdue: true, lastOutcome: "unavailable" }, allowedActions: ["inspect", "delete"] },
      { name: "work-vm-c3", state: "running", sharing: "private", currentOperation: { jobId: "j", kind: "vm-shutdown" } },
      { name: "work-vm-d4", state: "running", deleting: true },
    ], NOW);
    deep("children: sorted by name", rows.map((r) => r.name), ["work-vm-a1", "work-vm-b2", "work-vm-c3", "work-vm-d4"]);
    ok("children: overdue is flagged with the reason", rows[0].overdue && /OVERDUE — unavailable/.test(rows[0].lease));
    ok("children: an off VM cannot be shut down but can be deleted", !rows[0].canShutdown && rows[0].canDelete);
    ok("children: a running shared child can be shut down and deleted", rows[1].canShutdown && rows[1].canDelete && rows[1].shared);
    ok("children: a busy child offers nothing and names the operation", !rows[2].canShutdown && !rows[2].canDelete && rows[2].busy && rows[2].operation === "vm-shutdown");
    ok("children: a deleting child is busy 'deleting'", rows[3].busy && rows[3].operation === "deleting" && !rows[3].canDelete);
    ok("children: an absent allowedActions refuses nothing (the route re-checks)", ha.childRows([{ name: "x", state: "running" }]).every((r) => r.canShutdown && r.canDelete));
    eq("card: local backend hides", ha.childrenCardState({ backend: "hyperv-local", supported: true, items: [] }), null);
    eq("card: unsupported host hides", ha.childrenCardState({ backend: "hyperv-remote", supported: false, items: [] }), null);
    const card = ha.childrenCardState({ backend: "hyperv-remote", supported: true, primary: "work-vm", items: [{ name: "work-vm-a1", state: "running" }], now: NOW });
    ok("card: supported renders the rows", card.visible && card.items.length === 1 && card.primary === "work-vm" && card.problem === "");
    const failed = ha.childrenCardState({ backend: "hyperv-remote", supported: true, primary: "work-vm", items: null, problem: "HTTP 500" });
    ok("card: a failed read on a supporting host keeps the card with the problem", failed.visible && failed.items.length === 0 && failed.problem === "HTTP 500");
  }
  {
    ok("shutdown: completed is the only success", ha.shutdownOutcome({ state: "succeeded", result: { outcome: "completed", name: "c" } }).ok);
    const un = ha.shutdownOutcome({ state: "failed", error: "guest-shutdown-unavailable" }, "c");
    ok("shutdown: unavailable is honest and names no force-off", !un.ok && /could NOT be shut down gracefully/.test(un.text) && /does not force it off/.test(un.text));
    const to = ha.shutdownOutcome({ state: "failed", error: "shutdown-timeout" }, "c");
    ok("shutdown: timeout is honest", !to.ok && /did not power off within/.test(to.text));
    ok("shutdown: superseded is reported as no change", /superseded/.test(ha.shutdownOutcome({ state: "succeeded", result: { outcome: "superseded" } }, "c").text));
    ok("shutdown: still running says so", /still running/.test(ha.shutdownOutcome({ id: "j1", state: "running" }, "c").text));
    ok("shutdown: a generic failure carries the error", /failed: boom/.test(ha.shutdownOutcome({ state: "failed", error: "boom" }, "c").text));
    const seq = [{ state: "running" }, { state: "running" }, { state: "succeeded", result: { outcome: "completed" } }];
    let i = 0;
    const c = { getJob: async () => seq[Math.min(i++, seq.length - 1)] };
    const final = await ha.awaitJob(c, "j", { attempts: 10, delayMs: 1, sleep: async () => {} });
    eq("awaitJob: polls to the terminal state", final.state, "succeeded");
    eq("awaitJob: ...in three reads", i, 3);
    i = 0;
    const partial = await ha.awaitJob(c, "j", { attempts: 2, delayMs: 1, sleep: async () => {} });
    eq("awaitJob: gives up after the attempts with the last body", partial.state, "running");
  }

  console.log("\n=== §10.4 confirmations ===");
  {
    const problem = apiErr(409, { code: "cascade-confirmation-required", cascadeToken: "0123456789abcdef0123456789abcdef", expiresAt: "2026-09-07T10:10:00Z",
      children: [{ name: "child-a", state: "running", sharing: "private", diskGb: 80, mediaCount: 0 }, { name: "child-b", state: "saved", sharing: "host", diskGb: 40, mediaCount: 1 }] });
    eq("cascade: recognised", ha.cascadeKindOf(problem), "required");
    eq("cascade: scope-changed recognised", ha.cascadeKindOf(apiErr(409, { code: "cascade-scope-changed" })), "scope-changed");
    eq("cascade: expired recognised", ha.cascadeKindOf(apiErr(409, { code: "cascade-token-expired" })), "expired");
    eq("cascade: other 409s are not cascades", ha.cascadeKindOf(apiErr(409, { code: "vm-deleting" })), null);
    const c = ha.cascadeConfirmation({ primary: "work-vm", problem });
    ok("cascade: names the primary and the count, including shared ones", /Deleting "work-vm" also deletes ALL of its 2 child VMs, including shared ones\./.test(c.detail));
    ok("cascade: says disks, saved state and media go permanently", /virtual disks, saved state and dedicated media are removed permanently/.test(c.detail));
    ok("cascade: lists every child with its state", /child-a\s+running\s+private\s+80 GB disk/.test(c.detail));
    ok("cascade: a shared child is highlighted loudly", /child-b\s+saved\s+SHARED HOST-WIDE \(other users may be using it\)\s+40 GB disk\s+1 dedicated media/.test(c.detail));
    ok("cascade: asks for the typed name and states the expiry", /Type the instance name to confirm\. \(This confirmation expires at 2026-09-07 10:10 UTC\.\)/.test(c.detail));
    eq("cascade: carries the token", c.cascadeToken, "0123456789abcdef0123456789abcdef");
    ok("cascade: the typed name is required", c.requireTypedName && c.typedName === "work-vm");
    eq("cascade: shared count", c.sharedCount, 1);
    eq("cascade: confirm label", c.confirmLabel, "Delete work-vm and 2 child VMs");
    const one = ha.cascadeConfirmation({ primary: "p", problem: { body: { children: [{ name: "x" }] } } });
    ok("cascade: singular wording for one child", /1 child VM,/.test(one.detail));
    const cd = ha.childDeleteConfirmation({ name: "child-b", sharing: "host", state: "running" });
    ok("child delete: names the child, its sharing and the permanent removal",
      /child-b is SHARED HOST-WIDE — other users may be using it and currently running/.test(cd.detail) && /disk, saved state and dedicated media are removed permanently/.test(cd.detail) && cd.shared);
    ok("child delete: a private child says private", /is private/.test(ha.childDeleteConfirmation({ name: "c", sharing: "private" }).detail));
  }

  console.log("\n=== offers ===");
  {
    const hosts = [{ url: "https://buildbox.example.local:7462", identity: "alice", role: "admin" }, { url: "https://other:7462", identity: "alice", role: "user" }];
    const insts = [{ name: "work-vm", backend: "hyperv-remote", service: { url: "buildbox.example.local" } }];
    const same = (a, b) => a.replace(/^https:\/\//, "").replace(/:7462$/, "").toLowerCase() === b.replace(/^https:\/\//, "").replace(/:7462$/, "").toLowerCase();
    const offers = ha.firstVmOffers({ hosts, instances: insts, sameUrl: same });
    deep("offers: only the host with zero own VMs", offers.map((o) => o.url), ["https://other:7462"]);
    eq("offers: none without hosts", ha.firstVmOffers({ hosts: [], instances: insts }).length, 0);
    const configured = { name: "cli-vm", backend: "hyperv-remote", service: { url: "https://cli.example:7462", auth: "token" } };
    const explicit = { url: "https://cli.example:7462", auth: "negotiate", fingerprint: "saved-pin" };
    deep("discovery: command-line hosts preserve auth without inventing a role", ha.discoverHosts([], [configured], same), [{ url: configured.service.url, auth: "token" }]);
    deep("discovery: explicit enrolment wins and shared hosts deduplicate", ha.discoverHosts([explicit], [configured, configured], same), [explicit]);
    deep("discovery: local instances do not invent a remote host", ha.discoverHosts([], [{ ...configured, backend: "hyperv-local" }], same), []);
    eq("hostEntryFor: finds the enrolment", ha.hostEntryFor(insts[0], hosts, same).identity, "alice");
    eq("hostEntryFor: a local instance has none", ha.hostEntryFor({ name: "agent-vm" }, hosts, same), null);
  }

  {
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN, isoCatalog: apiErr(404), media: [{ id: "retained" }] });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect(); await m.load("media");
    eq("catalog 404 preserves child inventory", m.state.media.items[0].id, "retained");
    eq("catalog 404 clears stale catalog", m.state.media.catalog, null);
    ok("catalog failure is reported separately", !!m.state.media.catalogProblem && !m.state.media.mediaProblem);
  }

  console.log("\n=== the model ===");
  {
    const c = fakeClient({
      health: HEALTH_FULL, whoami: ME_ADMIN,
      hostStatus: { version: { commit: "abc" }, health: {}, capacity: {}, activeJobs: [] },
      vms: VMS, users: [{ name: "bob", role: "user" }, { name: "alice", role: "admin" }],
      isoCatalog: { mode: "native" }, media: [{ id: "m1", references: 0 }],
      jobs: [{ id: "j1", kind: "vm-shutdown", state: "running" }], audit: [{ at: "2026-09-07T09:00:00Z", action: "x" }],
      hostConfig: { capacity: { value: { mode: "observe" }, source: "stored" } }, hostCapabilities: { backend: "hyperv-local", capabilities: {} },
      updatesStatus: { installed: { commit: "abc" } },
    });
    const m = ha.createHostAdminModel({ client: c, host: H, url: "https://" + H + ":7462", backend: "hyperv-remote", now: () => NOW });
    eq("model: starts unavailable until detected", m.state.mode, "unavailable");
    await m.detect();
    eq("model: detect -> admin", m.state.mode, "admin");
    ok("model: every tab available", m.state.tabs.every((t) => t.available));
    await m.load("overview");
    eq("model: overview loaded", m.state.overview.version.commit, "abc");
    await m.load("vms");
    eq("model: vms rows", m.state.vms.rows.length, 4);
    await m.load("users");
    deep("model: users sorted", m.state.users.rows.map((u) => u.name), ["alice", "bob"]);
    await m.load("media");
    eq("model: media items", m.state.media.items.length, 1);
    eq("model: media catalog", m.state.media.catalog.mode, "native");
    await m.load("operations");
    eq("model: jobs", m.state.operations.jobs.length, 1);
    eq("model: audit", m.state.operations.audit.length, 1);
    await m.load("config");
    eq("model: config sections", m.state.config.sections.length, 7);
    ok("model: capabilities loaded", !!m.state.config.capabilities);
    await m.load("maintenance");
    eq("model: update view", m.state.maintenanceTab.installed.commit, "abc");
    ok("model: not busy after loads", !m.state.busy);

    // Mutations go to the right routes with the right bodies.
    let r = await m.perform("shutdownVm", { name: "work-vm-a1b2" });
    let call = c.calls[c.calls.length - 1];
    ok("model: Shut down is a GRACEFUL lifecycle shutdown", call.method === "lifecycle" && call.args[0] === "work-vm-a1b2" && call.args[1].action === "shutdown");
    ok("model: ...never a power action", !c.calls.some((x) => x.method === "power"));
    ok("model: ...and reports the request", r.ok && /Graceful shutdown of work-vm-a1b2 requested/.test(m.state.notice.text));
    r = await m.perform("deleteVm", { name: "work-vm-a1b2" });
    call = c.calls[c.calls.length - 1];
    ok("model: Delete without a token sends no body", call.method === "deleteVm" && call.args[1] === undefined && r.ok);
    r = await m.perform("deleteVm", { name: "work-vm", cascadeToken: "tok" });
    call = c.calls[c.calls.length - 1];
    deep("model: a cascade delete carries the token", call.args[1], { cascade: { token: "tok" } });
    r = await m.perform("saveAllowance", { name: "bob", form: { maxRetainedChildren: "2" } });
    call = c.calls[c.calls.length - 1];
    ok("model: allowance PUT with nulls for inherit", call.method === "putUserAllowance" && call.args[0] === "bob" && call.args[1].maxRetainedChildren === 2 && call.args[1].cpuBudget === null);
    const putsBefore = c.calls.filter((x) => x.method === "putUserAllowance").length;
    r = await m.perform("saveAllowance", { name: "bob", form: { maxRetainedChildren: "x" } });
    ok("model: an invalid allowance never reaches the service", !r.ok && r.problems.length === 1 && c.calls.filter((x) => x.method === "putUserAllowance").length === putsBefore);
    r = await m.perform("updateUser", { name: "bob", form: { role: "admin" } });
    deep("model: user update body", c.calls[c.calls.length - 1].args[1], { role: "admin" });
    r = await m.perform("saveConfig", { sections: [{ key: "capacity", text: '{"mode":"enforce"}' }] });
    ok("model: config PUT", c.calls[c.calls.length - 1].method === "putHostConfig" && r.ok);
    r = await m.perform("saveConfig", { sections: [{ key: "capacity", text: "{bad" }] });
    ok("model: invalid config JSON lands inline, not on the wire", !r.ok && m.state.config.problems[0].field === "capacity");
    r = await m.perform("updatesApply", { updateId: "u1" });
    deep("model: apply body", c.calls[c.calls.length - 1].args[0], { updateId: "u1" });
    r = await m.perform("updatesResolve", { updateId: "u1", action: "reboot" });
    ok("model: a bad resolve action is refused locally", !r.ok && /commit, abort or close/.test(r.error));
    r = await m.perform("rotateVmToken", { name: "work-vm" });
    ok("model: rotation defaults to primary kind", c.calls[c.calls.length - 1].args[1].kind === "primary");
    r = await m.perform("nope", {});
    ok("model: unknown action refused", !r.ok && /unknown host-administration action/.test(r.error));
  }
  {
    // A cascade confirmation surfaces as data, not as an error.
    const c = fakeClient({
      health: HEALTH_FULL, whoami: ME_ADMIN,
      deleteVm: (name, body) => {
        if (body && body.cascade && body.cascade.token === "t1") return { jobId: "j7" };
        throw apiErr(409, { code: "cascade-confirmation-required", cascadeToken: "t1", expiresAt: "2026-09-07T10:10:00Z", children: [{ name: "c", state: "running", sharing: "host", diskGb: 10 }] });
      },
    });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect();
    let r = await m.perform("deleteVm", { name: "work-vm" });
    ok("model: 409 cascade-confirmation-required becomes a confirmation", !r.ok && r.cascade && r.cascade.cascadeToken === "t1" && r.cascade.children.length === 1);
    ok("model: ...not a notice", m.state.notice === null);
    r = await m.perform("deleteVm", { name: "work-vm", cascadeToken: r.cascade.cascadeToken });
    ok("model: confirmed with the token it is accepted", r.ok && r.jobId === "j7");
  }
  {
    // A later 403 flips to the user state; a 503 sets the maintenance banner and
    // disables mutations; a plain error is a notice.
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN, users: apiErr(403, null), hostStatus: apiErr(500, { title: "boom", detail: "db" }) });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect();
    await m.load("overview");
    ok("model: a 500 on a load is a notice, mode unchanged", m.state.mode === "admin" && m.state.notice && m.state.notice.level === "error");
    await m.load("users");
    eq("model: a 403 on a load flips to user", m.state.mode, "user");
    ok("model: ...with the reason", /role has changed/.test(m.state.message));
    const r = await m.perform("mediaCleanup", {});
    ok("model: a user may not perform admin actions", !r.ok && /not an administrator/.test(r.error));
    ok("model: ...and nothing was sent", !c.calls.some((x) => x.method === "mediaCleanup"));
  }
  {
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN, hostStatus: apiErr(503, { code: "maintenance", phase: "draining", retryAfterSeconds: 5 }) });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect();
    await m.load("overview");
    ok("model: a 503 sets the maintenance banner", m.state.maintenance && m.state.maintenance.phase === "draining" && m.state.mode === "admin");
    const r = await m.perform("mediaCleanup", {});
    ok("model: mutations are disabled while updating", !r.ok && /updating/.test(r.error) && !c.calls.some((x) => x.method === "mediaCleanup"));
    eq("model: ...and the panel polls every 5 s", ha.pollIntervalMs(m.state), 5000);
  }
  {
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_USER });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect();
    eq("model: an ordinary user is the user state", m.state.mode, "user");
    await m.load("vms");
    ok("model: a user loads nothing (the module is absent)", m.state.vms === null && !c.calls.some((x) => x.method === "vms"));
  }
  {
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN, media: apiErr(404, { code: "not-found" }) });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote" });
    await m.detect();
    m.state.features = ha.featureSet({ apiFeatures: ["host-admin"] });
    m.state.tabs = ha.tabsFor(m.state);
    await m.load("media");
    ok("model: a host without the media feature says so instead of erroring", m.state.media && /not available on this host version/.test(m.state.media.mediaProblem) && !c.calls.some((x) => x.method === "media"));
    await m.load("maintenance");
    ok("model: an unavailable tab loads nothing", m.state.maintenanceTab === null && !c.calls.some((x) => x.method === "updatesStatus"));
  }
  {
    const m = ha.createHostAdminModel({ client: null, host: H, backend: "hyperv-remote", problem: "the API token for the host is gone" });
    await m.detect();
    eq("model: no client is unavailable", m.state.mode, "unavailable");
    const r = await m.perform("refresh", {});
    ok("model: ...and every action reports why", !r.ok && /token/.test(r.error));
  }
  {
    // Secrets: a one-time token comes back to the caller and never lands in the log.
    const lines = [];
    const c = fakeClient({ health: HEALTH_FULL, whoami: ME_ADMIN, issueUserToken: { id: "t1", token: "PLAINTEXT-SECRET" }, rotateVmToken: { vmToken: "VM-SECRET", kind: "primary" } });
    const m = ha.createHostAdminModel({ client: c, host: H, backend: "hyperv-remote", log: (l) => lines.push(l) });
    await m.detect();
    const r = await m.perform("issueToken", { name: "bob", label: "laptop" });
    eq("secrets: the plaintext is returned once", r.secret, "PLAINTEXT-SECRET");
    const r2 = await m.perform("rotateVmToken", { name: "work-vm" });
    eq("secrets: the VM token is returned once", r2.secret, "VM-SECRET");
    const dump = JSON.stringify(m.state) + lines.join("\n");
    ok("secrets: neither plaintext is in the state or the log", dump.indexOf("PLAINTEXT-SECRET") < 0 && dump.indexOf("VM-SECRET") < 0);
  }

  console.log(`\n  host-administration unit tests — ${pass}/${pass + fail} passed\n`);
  process.exit(fail ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
