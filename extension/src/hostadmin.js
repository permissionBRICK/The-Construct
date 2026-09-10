"use strict";
// HOST ADMINISTRATION — the pure half of the extension's host-administration module
// (docs/plans/host-administration-contracts.md §10; the VS Code adapter is
// src/hostadmin-ui.js, the webview media/hostadmin.*).
//
// Built to the client-tool-module rules (extension/ARCHITECTURE.md "File layout"):
//   • NO `require("vscode")`, no sockets, no processes. The ONE dependency is a
//     src/remotehost.js client, injected — so every state transition, every view-model
//     and every confirmation text unit-tests under plain node against a fake client
//     (extension/test/hostadmin.test.js).
//   • It is a MANAGEMENT UI, never an authority: every allowed-action list it renders is
//     presentation (§2.2 "allowedActions is presentation only; every route re-checks"),
//     and every mutation goes to the service, which decides.
//   • Identity is resolved PER HOST and never cached across hosts (§10.3): a model is
//     bound to one enrolled host, and a switch builds a new one.
//
// What lives here:
//   detection      featureSet / classifyHost / resolveHostState / applyRefusal — the
//                  §10.3 state table, in detection order
//   view-models    DTO → what the webview renders (bytes, leases, guest facts, capacity
//                  bars, jobs, users, media, config sections, update status)
//   forms          allowance / overrides / user / config / lifetime parsing with the
//                  contract's validation, so a bad value is refused HERE with a reason
//                  before it becomes a 400
//   confirmations  the §10.4 cascade text and the child Delete text, as DATA
//   the model      createHostAdminModel: one host's admin state machine — detect, load a
//                  tab, perform an action — every step re-classifying on refusal

/** The `apiFeatures` names a current service advertises (§3.4). */
const FEATURE_NAMES = ["host-admin", "children", "media", "console", "updates", "network"];

/** The exhaustive `ChildAction` enum of §2.2, for rendering `allowedActions`. */
const CHILD_ACTIONS = [
  "inspect", "start", "shutdown", "save", "restart", "delete", "share", "renew",
  "hardware", "media", "console", "forwardClient", "forwardHost", "addresses", "overrides", "rotateToken",
];

/** The host-config sections of §1.5, in display order. */
const CONFIG_SECTIONS = ["capacity", "userDefaults", "userCaps", "lifecycle", "media", "network", "updates"];

/** The tabs of §10.2 and the feature each one needs. `host-admin` gates the module. */
const TABS = [
  { id: "overview", label: "Overview", feature: "host-admin" },
  { id: "vms", label: "VMs", feature: "host-admin" },
  { id: "users", label: "Users", feature: "host-admin" },
  { id: "media", label: "Media", feature: "host-admin" },
  { id: "operations", label: "Operations", feature: "host-admin" },
  { id: "config", label: "Configuration", feature: "host-admin" },
  { id: "maintenance", label: "Maintenance", feature: "updates" },
];

/** How often the panel re-asks `/health` while the host is updating (§10.3). */
const MAINTENANCE_POLL_MS = 5000;

const GIB = 1024 * 1024 * 1024;

// ── Small helpers ────────────────────────────────────────────────────────────

const str = (v) => (v == null ? "" : String(v)).trim();
const num = (v) => (typeof v === "number" && Number.isFinite(v) ? v : (v != null && v !== "" && Number.isFinite(Number(v)) ? Number(v) : null));
const errStatus = (e) => (e && typeof e.status === "number" ? e.status : null);
const errCode = (e) => str(e && e.code) || str(e && e.body && typeof e.body === "object" && e.body.code);
const errText = (e) => (e && e.message ? String(e.message) : String(e));

/** Bytes as "8.0 GiB" / "512 MiB" / "—" for null. Pure. */
function formatBytes(bytes) {
  const n = num(bytes);
  if (n === null) return "—";
  if (n >= GIB) return (n / GIB).toFixed(n >= 10 * GIB ? 0 : 1) + " GiB";
  if (n >= 1024 * 1024) return Math.round(n / (1024 * 1024)) + " MiB";
  if (n >= 1024) return Math.round(n / 1024) + " KiB";
  return n + " B";
}

/** An ISO timestamp as "2026-09-07 10:12 UTC", "—" when absent/unparseable. Pure. */
function formatWhen(iso) {
  const s = str(iso);
  if (!s) return "—";
  const t = Date.parse(s);
  if (!Number.isFinite(t)) return s;
  const d = new Date(t);
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())} ${p(d.getUTCHours())}:${p(d.getUTCMinutes())} UTC`;
}

/** Seconds as "2h 5m" / "45s" / "3d 2h". Pure. */
function formatDuration(seconds) {
  const s = Math.max(0, Math.round(num(seconds) || 0));
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m`;
  return `${s}s`;
}

/** Percentage of `part` in `whole`, clamped to [0, 100]; 0 when the whole is unknown. Pure. */
function pct(part, whole) {
  const p = num(part), w = num(whole);
  if (p === null || w === null || w <= 0) return 0;
  return Math.max(0, Math.min(100, Math.round((p / w) * 100)));
}

// ── Feature detection (§3.4, §10.3) ──────────────────────────────────────────

/**
 * The feature flags a `/health` body advertises. Every flag is false for a body without
 * an `apiFeatures` array — that is what "not available on this host version" means. Pure.
 */
function featureSet(health) {
  const list = health && Array.isArray(health.apiFeatures) ? health.apiFeatures.map((f) => str(f)) : [];
  return {
    hostAdmin: list.indexOf("host-admin") >= 0,
    children: list.indexOf("children") >= 0,
    media: list.indexOf("media") >= 0,
    console: list.indexOf("console") >= 0,
    updates: list.indexOf("updates") >= 0,
    network: list.indexOf("network") >= 0,
  };
}

/** Is this error the service's maintenance refusal (`503 maintenance`, §7.4)? Pure. */
function isMaintenanceError(e) {
  return errStatus(e) === 503 && (errCode(e) === "maintenance" || errCode(e) === "");
}

/** The maintenance descriptor of a health body or a 503 error, or null. Pure. */
function maintenanceOf(health, error) {
  if (health && str(health.status).toLowerCase() === "maintenance") {
    const m = health.maintenance && typeof health.maintenance === "object" ? health.maintenance : {};
    return { phase: str(m.phase) || "maintenance", retryAfterSeconds: num(m.retryAfterSeconds), updateId: str(m.updateId) || null };
  }
  if (error && isMaintenanceError(error)) {
    const b = error.body && typeof error.body === "object" ? error.body : {};
    return { phase: str(b.phase) || "maintenance", retryAfterSeconds: num(b.retryAfterSeconds), updateId: str(b.updateId) || null };
  }
  return null;
}

/**
 * The §10.3 detection table, in its order, as one pure function of what the two probes
 * answered. `input`:
 *   backend      the instance's backend ("hyperv-local" ⇒ nothing is rendered)
 *   host         the host name, for the messages
 *   health       the `/health` body, or undefined
 *   healthError  the error `/health` threw (carries `status`), or undefined
 *   whoami       the `/whoami` body, or undefined
 *   whoamiError  its error, or undefined
 *
 * Returns { mode, message, features, maintenance, identity, retryable }, where mode is one of
 *   local · unavailable · old-service · sign-in · denied · user · admin
 * The admin module is rendered ONLY for `admin`; `user` is the ordinary-user state (the
 * module is absent, not disabled); the rest are the useful states the contract asks for.
 */
function classifyHost(input = {}) {
  const host = str(input.host) || "the host";
  const out = {
    mode: "unavailable", message: "", features: featureSet(null), maintenance: null,
    identity: null, retryable: false,
  };
  const backend = str(input.backend).toLowerCase();
  if (backend && backend !== "hyperv-remote") {
    return { ...out, mode: "local", message: "This instance runs on this PC's Hyper-V; there is no host service to administer." };
  }
  // 1. /health
  const he = input.healthError;
  if (he) {
    const status = errStatus(he);
    if (status === 404) {
      return {
        ...out, mode: "old-service",
        message: `This host's service predates host administration. Update it on the host (service/host/Install-ConstructHost.ps1); no update can be driven from here.`,
      };
    }
    if (isMaintenanceError(he)) {
      out.maintenance = maintenanceOf(null, he);
    } else {
      return { ...out, mode: "unavailable", retryable: true, message: `Cannot reach ${host}: ${errText(he)}` };
    }
  } else {
    out.features = featureSet(input.health);
    out.maintenance = maintenanceOf(input.health, null);
    if (!out.features.hostAdmin) {
      return {
        ...out, mode: "old-service",
        message: `This host's service answers, but does not offer host administration (its apiFeatures lack "host-admin"). Update it on the host; no update can be driven from here.`,
      };
    }
  }
  // 2. /whoami
  const we = input.whoamiError;
  if (we) {
    const status = errStatus(we);
    if (status === 401) return { ...out, mode: "sign-in", message: `${host} rejected the stored credential. Sign in again with "The Construct: Add Remote Host".` };
    if (status === 403) return { ...out, mode: "denied", message: `You are not enrolled on ${host}; ask its administrator.` };
    if (isMaintenanceError(we)) { out.maintenance = out.maintenance || maintenanceOf(null, we); }
    else return { ...out, mode: "unavailable", retryable: true, message: `Cannot reach ${host}: ${errText(we)}` };
  }
  const me = input.whoami && typeof input.whoami === "object" ? input.whoami : {};
  const identity = {
    name: str(me.name), role: str(me.role).toLowerCase(),
    known: me.known !== false, enabled: me.enabled !== false,
    maxVms: num(me.maxVms), effective: me.effective && typeof me.effective === "object" ? me.effective : null,
  };
  out.identity = identity;
  if (we && isMaintenanceError(we)) {
    // The identity could not be read during a maintenance window: neither admin nor
    // user is established, so nothing is offered until the host is back.
    return { ...out, mode: "unavailable", retryable: true, message: `${host} is updating (${out.maintenance.phase}); reconnecting…` };
  }
  if (!identity.known || !identity.enabled) {
    return { ...out, mode: "denied", message: `${identity.name || "This identity"} is not enrolled (or is disabled) on ${host}; ask its administrator.` };
  }
  if (identity.role === "admin") return { ...out, mode: "admin", message: "" };
  return { ...out, mode: "user", message: `${identity.name || "This identity"} is not an administrator of ${host}.` };
}

/**
 * Run the two probes against a client and classify. Never rejects: every failure is an
 * input to classifyHost. `opts.backend`/`opts.host` pass through; `client` is a
 * src/remotehost.js client (or any object with `health()` and `whoami()`).
 */
async function resolveHostState(client, opts = {}) {
  const input = { backend: opts.backend, host: opts.host || (client && client.host) };
  if (str(input.backend).toLowerCase() && str(input.backend).toLowerCase() !== "hyperv-remote") return classifyHost(input);
  if (!client) return classifyHost({ ...input, healthError: Object.assign(new Error(opts.problem || "no client for this host"), { status: 0 }) });
  try { input.health = await client.health(); } catch (e) { input.healthError = e; }
  if (input.healthError && errStatus(input.healthError) === 404) return classifyHost(input);
  if (input.healthError && !isMaintenanceError(input.healthError)) return classifyHost(input);
  try { input.whoami = await client.whoami(); } catch (e) { input.whoamiError = e; }
  return classifyHost(input);
}

/**
 * A later refusal on an admin call re-classifies the host (§10.3: "a later 403 on any
 * admin call (role changed) flips the panel to the ordinary-user state immediately").
 * Returns the state to show now — the input state when the error changes nothing. Pure.
 */
function applyRefusal(state, error, host) {
  const status = errStatus(error);
  const h = str(host || (state && state.host)) || "the host";
  if (status === 403) return { ...state, mode: "user", message: `${h} refused an administrator action: your role has changed. Ask its administrator.` };
  if (status === 401) return { ...state, mode: "sign-in", message: `${h} rejected the stored credential. Sign in again with "The Construct: Add Remote Host".` };
  if (isMaintenanceError(error)) return { ...state, maintenance: maintenanceOf(null, error) };
  if (status === 0) return { ...state, mode: "unavailable", retryable: true, message: `Cannot reach ${h}: ${errText(error)}` };
  return state;
}

/** The webview's tab list for a state: each tab says whether the host version has it. Pure. */
function tabsFor(state) {
  const f = (state && state.features) || featureSet(null);
  return TABS.map((t) => {
    const key = t.feature === "host-admin" ? "hostAdmin" : t.feature;
    const available = !!f[key];
    return { id: t.id, label: t.label, available, reason: available ? "" : "not available on this host version" };
  });
}

/** Refresh VM usage while its tab is active; keep editable tabs stable. Pure. */
function pollIntervalMs(state) {
  if (state && state.maintenance) return MAINTENANCE_POLL_MS;
  if (state && (state.updatePending || ["checking", "draining", "handedOff", "applying"].includes(state.maintenanceTab?.current?.state))) return MAINTENANCE_POLL_MS;
  if (state?.mode !== "admin") return null;
  return state.activeTab === "vms" ? 10000 : state.features?.updates ? 60000 : null;
}

// ── View-models ──────────────────────────────────────────────────────────────

/** The lease column of a child: what the user needs to act on (§5.5). Pure. */
function leaseText(lease, now) {
  if (!lease || typeof lease !== "object") return "";
  const state = str(lease.state).toLowerCase();
  if (lease.overdue === true || state === "overdue") {
    const why = str(lease.lastOutcome);
    return "OVERDUE" + (why ? ` — ${why}` : "") + " (shutdown due; retried)";
  }
  if (state === "unlimited") return "no expiry";
  if (state === "inactive") return "not started";
  if (state === "expired") return "expired" + (lease.expiresAt ? ` at ${formatWhen(lease.expiresAt)}` : "");
  if (state === "active") {
    const t = Date.parse(str(lease.expiresAt));
    const n = typeof now === "number" ? now : Date.now();
    if (Number.isFinite(t)) {
      const left = Math.round((t - n) / 1000);
      return left > 0 ? `expires in ${formatDuration(left)} (${formatWhen(lease.expiresAt)})` : `due since ${formatWhen(lease.expiresAt)}`;
    }
    return "active";
  }
  return state || "";
}

/** Guest facts, honestly: every unknown value prints "unknown" (§8.3). Pure. */
function guestText(guest) {
  const g = guest && typeof guest === "object" ? guest : {};
  const commit = str(g.constructCommit);
  const parts = [
    "Construct " + (commit ? commit.slice(0, 12) : "unknown"),
    "provisioned " + (g.provisionedAt ? formatWhen(g.provisionedAt) : "unknown"),
    "reinstalled " + (g.reinstalledAt ? formatWhen(g.reinstalledAt) : "unknown"),
  ];
  if (g.lastAttemptOutcome) parts.push(`last attempt ${str(g.lastAttemptOutcome)} ${g.lastAttemptAt ? formatWhen(g.lastAttemptAt) : ""}`.trim());
  return parts.join(" · ");
}

/** "4 vCPU · 8 GiB · 80 GB" from the hardware block, "—" when unknown. Pure. */
function resourcesText(hw) {
  const h = hw && typeof hw === "object" ? hw : null;
  if (!h) return "—";
  const parts = [];
  if (num(h.cpus) !== null) parts.push(`${h.cpus} vCPU`);
  if (num(h.ramMb) !== null) parts.push(formatBytes(h.ramMb * 1024 * 1024));
  if (num(h.diskGb) !== null) parts.push(`${h.diskGb} GB disk`);
  return parts.length ? parts.join(" · ") : "—";
}

/** The current operation of a VM as one phrase, "" when idle. Pure. */
function operationText(op) {
  if (!op || typeof op !== "object") return "";
  const kind = str(op.kind) || "job";
  return kind + (op.phase ? ` (${str(op.phase)})` : "") + (op.initiator ? ` by ${str(op.initiator)}` : "");
}

/** Host readings, keeping unknown values distinct from zero and allocation. */
function resourceUsageView(usage, now) {
  const u = usage || {};
  const at = Date.parse(u.observedAt);
  const known = Number.isFinite(at);
  const stale = !!u.stale || (known && now - at > 30000);
  const rawCpu = num(u.cpuUsagePercent);
  const cpu = rawCpu !== null && rawCpu >= 0 && rawCpu <= 100 ? rawCpu : null;
  const demand = num(u.memoryDemandBytes);
  const assigned = num(u.memoryAssignedBytes);
  return {
    cpuPercent: cpu,
    ramPercent: demand !== null && assigned > 0 ? pct(demand, assigned) : null,
    cpu: cpu !== null ? `${Math.round(cpu * 10) / 10}% CPU` : "CPU usage unavailable",
    ram: demand !== null ? `${formatBytes(demand)} RAM demand / ${formatBytes(assigned)} assigned`
      : assigned !== null ? `${formatBytes(assigned)} RAM assigned · demand unavailable` : "RAM usage unavailable",
    disk: num(u.diskFileBytes) !== null ? `${formatBytes(u.diskFileBytes)} disk on host` : "Disk usage unavailable",
    sample: known ? `${stale ? "Stale · last sample" : "Sampled"} ${new Date(at).toISOString().slice(11, 19)} UTC` : "Usage unavailable on this host",
    stale,
  };
}

/** One inventory row (`VmResponse` with the §8.3 additive fields). Pure. */
function toVmRow(vm, now) {
  const v = vm && typeof vm === "object" ? vm : {};
  const kind = str(v.kind).toLowerCase() || "primary";
  const actions = Array.isArray(v.allowedActions) ? v.allowedActions.map(str).filter((a) => CHILD_ACTIONS.indexOf(a) >= 0) : [];
  return {
    name: str(v.name),
    owner: str(v.owner),
    kind,
    parent: str(v.parent),
    sharing: str(v.sharing).toLowerCase() || "private",
    shared: v.shared === true,
    state: str(v.state).toLowerCase() || "unknown",
    tokenKind: str(v.tokenKind) || null,
    deleting: v.deleting === true,
    childCreationClosed: v.childCreationClosed === true,
    resources: resourcesText(v.hardware || { cpus: v.cpu, ramMb: num(v.ramGb) === null ? null : v.ramGb * 1024, diskGb: v.diskGb }),
    usage: resourceUsageView(v.resourceUsage, now),
    lease: leaseText(v.lease, now),
    overdue: !!(v.lease && (v.lease.overdue === true || str(v.lease.state).toLowerCase() === "overdue")),
    operation: operationText(v.currentOperation),
    operationJobId: v.currentOperation && v.currentOperation.jobId ? str(v.currentOperation.jobId) : "",
    guest: guestText(v.guest),
    reservations: v.reservations && typeof v.reservations === "object"
      ? `${formatBytes(v.reservations.ramBytes)} RAM · ${num(v.reservations.cpus) === null ? "—" : v.reservations.cpus} vCPU · ${formatBytes(v.reservations.storageBytes)} storage`
      : "—",
    children: Array.isArray(v.children) ? v.children.map(str).filter(Boolean) : [],
    allowedActions: actions,
    media: Array.isArray(v.media) ? v.media.length : 0,
  };
}

/** The VMs tab: rows grouped so every child sits under its parent. Pure. */
function toVmRows(list, now) {
  const rows = (Array.isArray(list) ? list : []).map((v) => toVmRow(v, now)).filter((r) => r.name);
  const primaries = rows.filter((r) => r.kind !== "child").sort((a, b) => a.name.localeCompare(b.name));
  const byParent = new Map();
  for (const r of rows.filter((r) => r.kind === "child")) {
    const list = byParent.get(r.parent) || [];
    list.push(r);
    byParent.set(r.parent, list);
  }
  const out = [];
  for (const p of primaries) {
    out.push(p);
    for (const c of (byParent.get(p.name) || []).sort((a, b) => a.name.localeCompare(b.name))) out.push(c);
    byParent.delete(p.name);
  }
  // Children whose parent is not in the list (an owner filter, a tombstoned parent
  // absent from the answer): still rows, never dropped.
  for (const list of byParent.values()) for (const c of list) out.push(c);
  return out;
}

/** Capacity bars of the Overview (`HostCapacitySummary`, §8.2). Pure. */
function toCapacityBars(summary) {
  const s = summary && typeof summary === "object" ? summary : {};
  const ram = s.ram && typeof s.ram === "object" ? s.ram : {};
  const cpu = s.cpu && typeof s.cpu === "object" ? s.cpu : {};
  const ramUsed = (num(ram.reservedBytes) || 0) + (num(ram.unmanagedBytes) || 0) + (num(ram.headroomBytes) || 0);
  const bars = [{
    id: "ram", label: "RAM",
    pct: pct(ramUsed, ram.totalBytes),
    text: `${formatBytes(ram.availableBytes)} available of ${formatBytes(ram.totalBytes)} (reserved ${formatBytes(ram.reservedBytes)}, unmanaged ${formatBytes(ram.unmanagedBytes)}, headroom ${formatBytes(ram.headroomBytes)})`,
  }, {
    id: "cpu", label: "CPU allocation",
    pct: num(cpu.budget) !== null ? pct(cpu.active, cpu.budget) : null,
    text: `${num(cpu.active) === null ? "—" : cpu.active} allocated vCPU` + (num(cpu.budget) !== null ? ` of a ${cpu.budget} budget` : ` on ${num(cpu.logical) === null ? "—" : cpu.logical} logical CPUs (no budget)`),
  }];
  for (const v of (Array.isArray(s.volumes) ? s.volumes : [])) {
    // Windows also inventories hidden EFI/recovery volumes. Keep their accounting
    // on the server, but don't present unmounted, unused partitions as VM storage.
    const unmounted = /^\\\\\?\\Volume\{[^}]+\}\\?$/i.test(str(v.root));
    if (unmounted && !(num(v.growthReservedBytes) > 0)) continue;
    // Show physical usage plus OS headroom. Future VM growth affects admission
    // availability, but has not consumed disk space and must not fill this bar.
    const used = (num(v.totalBytes) || 0) - (num(v.freeBytes) || 0) + (num(v.headroomBytes) || 0);
    bars.push({
      id: "vol:" + str(v.root), label: unmounted ? "Storage (unmounted volume)" : `Storage ${str(v.root)}`,
      pct: pct(used, v.totalBytes),
      text: `${formatBytes(v.availableBytes)} available of ${formatBytes(v.totalBytes)} (free ${formatBytes(v.freeBytes)}, growth reserved ${formatBytes(v.growthReservedBytes)}, headroom ${formatBytes(v.headroomBytes)})`,
    });
  }
  return bars;
}

/** The Overview tab from `HostStatusResponse` (§8.2). Pure. */
function toOverview(status) {
  const s = status && typeof status === "object" ? status : {};
  const version = s.version && typeof s.version === "object" ? s.version : {};
  const health = s.health && typeof s.health === "object" ? s.health : {};
  const cap = s.capacity && typeof s.capacity === "object" ? s.capacity : {};
  const maint = s.maintenance && typeof s.maintenance === "object" ? s.maintenance : {};
  const problems = [];
  if (str(health.hypervisor) && str(health.hypervisor) !== "ok") problems.push(`hypervisor ${str(health.hypervisor)}`);
  if (str(health.database) && str(health.database) !== "ok") problems.push(`database ${str(health.database)}`);
  if (str(health.media) && str(health.media) !== "ok") problems.push(`media ${str(health.media)}`);
  if (str(health.inventory) && str(health.inventory) !== "complete") problems.push(`inventory ${str(health.inventory)}`);
  return {
    version: {
      commit: str(version.commit) || "unknown",
      packageVersion: str(version.packageVersion) || "unknown",
      installedAt: formatWhen(version.installedAt),
      source: str(version.source) || "unknown",
    },
    health: { hypervisor: str(health.hypervisor) || "unknown", database: str(health.database) || "unknown", media: str(health.media) || "unknown", inventory: str(health.inventory) || "unknown" },
    problems,
    capacityMode: str(s.capacityMode).toLowerCase() === "enforce" ? "enforce" : "observe",
    capacity: toCapacityBars(cap),
    capacityEpoch: { epoch: str(cap.epoch), observedAt: formatWhen(cap.observedAt), complete: cap.complete !== false },
    maintenance: str(maint.phase) && str(maint.phase) !== "open"
      ? { phase: str(maint.phase), since: formatWhen(maint.since), updateId: str(maint.updateId) || null }
      : null,
    activeJobs: (Array.isArray(s.activeJobs) ? s.activeJobs : []).map((j) => ({
      id: str(j.id), kind: str(j.kind), vmName: str(j.vmName), owner: str(j.owner), initiator: str(j.initiator), phase: str(j.phase), created: formatWhen(j.created),
    })),
    leaseOverdueCount: num(s.leaseOverdueCount) || 0,
    unmanagedVmCount: num(s.unmanagedVmCount) || 0,
  };
}

/** Effective allowance as one line of text. Pure. */
function allowanceText(eff) {
  const e = eff && typeof eff === "object" ? eff : null;
  if (!e) return "—";
  const parts = [
    `${num(e.maxPrimaries) === null ? "—" : e.maxPrimaries} primaries`,
    e.allowChildCreation === false ? "no children" : `${num(e.maxRetainedChildren) === null ? "—" : e.maxRetainedChildren} children`,
    `CPU ${num(e.cpuBudget) === null ? "no budget" : e.cpuBudget}`,
    `RAM ${num(e.ramBudgetBytes) === null ? "no budget" : formatBytes(e.ramBudgetBytes)}`,
    `storage ${num(e.storageBudgetBytes) === null ? "no budget" : formatBytes(e.storageBudgetBytes)}`,
    `lifetime ≤ ${num(e.maxChildLifetimeSeconds) === null ? "unlimited" : formatDuration(e.maxChildLifetimeSeconds)}` + (e.allowNeverLifetime === false ? " (no 'never')" : ""),
    e.allowSharing === false ? "no sharing" : "sharing",
    e.allowHostForwards === false ? "no host forwards" : "host forwards",
  ];
  const u = e.usage && typeof e.usage === "object" ? e.usage : null;
  if (u) parts.push(`in use: ${num(u.primaries) || 0} primaries, ${num(u.children) || 0} children, ${num(u.cpus) || 0} vCPU, ${formatBytes(u.ramBytes)} RAM, ${formatBytes(u.storageBytes)} storage`);
  return parts.join(" · ");
}

/** One Users row (`UserDetailResponse`, §8.4). Pure. */
function toUserRow(user) {
  const u = user && typeof user === "object" ? user : {};
  const vms = u.vms && typeof u.vms === "object" ? u.vms : {};
  return {
    name: str(u.name),
    role: str(u.role).toLowerCase() || "user",
    enabled: u.enabled !== false,
    maxVms: num(u.maxVms),
    allowHostForwards: u.allowHostForwards !== false,
    created: formatWhen(u.created),
    primaries: num(vms.primaries) || 0,
    children: num(vms.children) || 0,
    tokens: num(u.tokens) || 0,
    allowance: allowanceForm(u.allowance),
    effective: allowanceText(u.effective),
  };
}

/** One Media row (`MediaItemResponse`, §8.10). Pure. */
function toMediaRow(item) {
  const m = item && typeof item === "object" ? item : {};
  return {
    id: str(m.id), owner: str(m.owner), name: str(m.name), role: str(m.role), source: str(m.source),
    sourceUrl: str(m.sourceUrl), state: str(m.state).toLowerCase() || "unknown",
    size: formatBytes(m.sizeBytes), reserved: formatBytes(m.reservedBytes),
    error: str(m.error), jobId: str(m.jobId), dedicatedTo: str(m.dedicatedTo),
    created: formatWhen(m.created), readyAt: formatWhen(m.readyAt),
    references: num(m.references) || 0,
    deletable: (num(m.references) || 0) === 0,
  };
}

/** The primary ISO catalog projection (§8.18). Pure. */
function toIsoCatalogView(catalog) {
  const c = catalog && typeof catalog === "object" ? catalog : {};
  const src = c.source && typeof c.source === "object" ? c.source : {};
  const cur = c.current && typeof c.current === "object" ? c.current : null;
  const last = c.lastBuild && typeof c.lastBuild === "object" ? c.lastBuild : null;
  return {
    mode: str(c.mode) || "unknown",
    source: {
      path: str(src.path), url: str(src.url), sha256Configured: src.sha256Configured === true,
      present: src.present === true, size: formatBytes(src.sizeBytes),
    },
    current: cur ? {
      fileName: str(cur.fileName), size: formatBytes(cur.sizeBytes), builtAt: formatWhen(cur.builtAt),
      sourceSha256: str(cur.sourceSha256), bootstrapKeyFingerprint: str(cur.bootstrapKeyFingerprint), hostnameSource: str(cur.hostnameSource),
    } : null,
    entries: (Array.isArray(c.entries) ? c.entries : []).map((e) => ({
      fileName: str(e.fileName), size: formatBytes(e.sizeBytes), isCurrent: e.isCurrent === true,
      builtAt: formatWhen(e.builtAt), sidecarReadable: e.sidecarReadable !== false,
    })),
    lastBuild: last ? { at: formatWhen(last.at), outcome: str(last.outcome), jobId: str(last.jobId) } : null,
  };
}

/** Terminal job states, as the service spells them. */
const TERMINAL_JOB_STATES = ["succeeded", "failed", "cancelled"];

/** One Operations row (`JobResponse` + §8.16 additive fields). Pure. */
function toJobRow(job) {
  const j = job && typeof job === "object" ? job : {};
  const state = str(j.state).toLowerCase() || "unknown";
  const kind = str(j.kind);
  const failed = state === "failed";
  return {
    id: str(j.id), kind, vmName: str(j.vmName), owner: str(j.owner), initiator: str(j.initiator),
    state, phase: str(j.phase), error: str(j.error),
    created: formatWhen(j.created), completed: formatWhen(j.completed || j.completedAt),
    terminal: TERMINAL_JOB_STATES.indexOf(state) >= 0,
    cancellable: TERMINAL_JOB_STATES.indexOf(state) < 0,
    // Retry buttons of §10.2: a failed delete is retried by deleting again (§8.8), a
    // failed cleanup by running the cleanup again. Nothing else is retried from here.
    retry: failed && (kind === "child-delete" || kind === "parent-cascade-delete") && str(j.vmName)
      ? { action: "deleteVm", name: str(j.vmName), kind: kind === "parent-cascade-delete" ? "primary" : "child" }
      : failed && kind === "media-cleanup" ? { action: "mediaCleanup" } : null,
  };
}

/** One audit row. Pure. */
function toAuditRow(entry) {
  const a = entry && typeof entry === "object" ? entry : {};
  return { at: formatWhen(a.at || a.timestamp || a.created), actor: str(a.actor), action: str(a.action), target: str(a.target), detail: str(a.detail) };
}

/** The Configuration tab: one editable JSON text per §1.5 section. Pure. */
function toConfigView(config) {
  const c = config && typeof config === "object" ? config : {};
  return CONFIG_SECTIONS.map((key) => {
    const section = c[key] && typeof c[key] === "object" ? c[key] : null;
    const value = section && section.value && typeof section.value === "object" ? section.value : section;
    const meta = section && typeof section === "object" ? section : {};
    const clean = {};
    if (value) for (const k of Object.keys(value)) if (k !== "source" && k !== "updatedAt") clean[k] = value[k];
    return {
      key,
      source: str(meta.source) || (section ? "stored" : "default"),
      updatedAt: meta.updatedAt ? formatWhen(meta.updatedAt) : "",
      expectedUpdatedAt: str(meta.updatedAt) || null,
      text: JSON.stringify(clean, null, 2),
      present: !!section,
    };
  });
}

/** The `/host/capabilities` body as rows for the Configuration tab (§3.4). Pure. */
function toCapabilityRows(body) {
  const b = body && typeof body === "object" ? body : {};
  const caps = b.capabilities && typeof b.capabilities === "object" ? b.capabilities : {};
  const rows = [];
  const walk = (prefix, obj) => {
    for (const k of Object.keys(obj)) {
      const v = obj[k];
      if (v && typeof v === "object" && !Array.isArray(v)) walk(prefix + k + ".", v);
      else if (k !== "notes") rows.push({ key: prefix + k, value: Array.isArray(v) ? v.map(str).join(", ") : str(v) });
    }
  };
  walk("", caps);
  const policy = b.policy && typeof b.policy === "object" ? b.policy : {};
  for (const k of Object.keys(policy)) rows.push({ key: "policy." + k, value: str(policy[k]) });
  return { backend: str(b.backend), rows, notes: Array.isArray(caps.notes) ? caps.notes.map(str) : [] };
}

/** The non-terminal update states (§11.8). */
const OPEN_UPDATE_STATES = ["checking", "staged", "draining", "handedOff", "applying", "interrupted", "recoveryFailed"];

/** Which update buttons apply to a status (§8.15). Pure. */
function updateActionsFor(status) {
  const s = status && typeof status === "object" ? status : {};
  const cur = s.current && typeof s.current === "object" ? s.current : null;
  const state = cur ? str(cur.state) : "";
  const inFlight = cur && OPEN_UPDATE_STATES.indexOf(state) >= 0;
  return {
    check: true,
    stage: !inFlight,
    apply: state === "staged",
    resume: state === "interrupted",
    cancel: !!cur && ["checking", "staged", "draining"].indexOf(state) >= 0,
    resolve: state === "interrupted" || state === "recoveryFailed",
  };
}

/** The Maintenance tab from `HostUpdateStatusResponse` (§11.8). Pure. */
function toUpdateView(status) {
  const s = status && typeof status === "object" ? status : {};
  const inst = s.installed && typeof s.installed === "object" ? s.installed : {};
  const cur = s.current && typeof s.current === "object" ? s.current : null;
  const latest = s.latestKnown && typeof s.latestKnown === "object" ? s.latestKnown : null;
  const row = (r) => ({
    updateId: str(r.updateId || r.id), commit: str(r.commit).slice(0, 12), packageVersion: str(r.packageVersion),
    state: str(r.state), phase: str(r.phase), started: formatWhen(r.started), finished: formatWhen(r.finished),
    error: str(r.error), actor: str(r.actor),
    phases: (Array.isArray(r.phases) ? r.phases : []).map((p) => ({ name: str(p.name), at: formatWhen(p.at), outcome: str(p.outcome), error: str(p.error) })),
    blockingJobs: (Array.isArray(r.blockingJobs) ? r.blockingJobs : []).map((b) => (typeof b === "object" ? str(b.id || b.jobId) : str(b))),
  });
  return {
    supportsAutoApply: s.supportsAutoApply === true,
    updateAvailable: !!latest?.commit && !!inst.commit && latest.commit !== inst.commit,
    installed: { commit: str(inst.commit) || "unknown", packageVersion: str(inst.packageVersion) || "unknown", installedAt: formatWhen(inst.installedAt), previousCommit: str(inst.previousCommit), source: str(inst.source) || "unknown" },
    current: cur ? row(cur) : null,
    history: (Array.isArray(s.history) ? s.history : []).map(row),
    latestKnown: latest ? { commit: str(latest.commit).slice(0, 12), packageVersion: str(latest.packageVersion), publishedAt: formatWhen(latest.publishedAt), checkedAt: formatWhen(latest.checkedAt), compatible: latest.compatible, reasons: latest.reasons } : null,
    recoveryRecord: s.recoveryRecord && typeof s.recoveryRecord === "object" ? JSON.stringify(s.recoveryRecord, null, 2) : "",
    actions: updateActionsFor(s),
  };
}

/** The `/host/updates/check` answer as text the tab shows. Pure. */
function checkResultText(res) {
  const r = res && typeof res === "object" ? res : {};
  const latest = r.latest && typeof r.latest === "object" ? r.latest : null;
  const inst = r.installed && typeof r.installed === "object" ? r.installed : {};
  if (!latest) return `No release found. Installed: ${str(inst.commit).slice(0, 12) || "unknown"} (${str(inst.packageVersion) || "unknown"}).`;
  const same = str(latest.commit) && str(latest.commit) === str(inst.commit);
  const head = same
    ? `Installed ${str(inst.commit).slice(0, 12)} is the latest release (${str(latest.releaseTag) || str(latest.packageVersion)}).`
    : `Latest ${str(latest.releaseTag) || str(latest.packageVersion)} (${str(latest.commit).slice(0, 12)}, published ${formatWhen(latest.publishedAt)}); installed ${str(inst.commit).slice(0, 12) || "unknown"}.`;
  if (latest.compatible === false) {
    return head + " NOT compatible: " + (Array.isArray(latest.reasons) && latest.reasons.length ? latest.reasons.map(str).join("; ") : "no reason given") + ".";
  }
  return head;
}

// ── Forms ────────────────────────────────────────────────────────────────────

/**
 * A lifetime string per §5.1: `never`, or `<n><unit>` with unit m/h/d, minimum 5m.
 * Returns { ok, seconds: number|null (never), text } or { ok: false, reason }. Pure.
 */
function parseLifetime(text) {
  const s = str(text).toLowerCase();
  if (!s) return { ok: false, reason: "a lifetime is required (e.g. 12h, 3d, or never)" };
  if (s === "never") return { ok: true, seconds: null, text: "never" };
  const m = /^([1-9][0-9]*)([mhd])$/.exec(s);
  if (!m) return { ok: false, reason: `"${text}" is not a lifetime: use <n>m, <n>h, <n>d or never` };
  const n = Number(m[1]);
  const seconds = n * (m[2] === "m" ? 60 : m[2] === "h" ? 3600 : 86400);
  if (seconds < 300) return { ok: false, reason: "the minimum lifetime is 5m" };
  return { ok: true, seconds, text: s };
}

/** Seconds → the shortest exact lifetime text ("12h", "90m", "3d"), "" for null. Pure. */
function lifetimeTextOf(seconds) {
  const n = num(seconds);
  if (n === null) return "";
  if (n % 86400 === 0) return `${n / 86400}d`;
  if (n % 3600 === 0) return `${n / 3600}h`;
  return `${Math.round(n / 60)}m`;
}

/** The allowance fields (§8.4 `UserAllowanceRequest`) as the editor shows them. Pure. */
function allowanceForm(stored) {
  const a = stored && typeof stored === "object" ? stored : {};
  const tri = (v) => (v === true ? "true" : v === false ? "false" : "");
  return {
    allowChildCreation: tri(a.allowChildCreation),
    maxRetainedChildren: num(a.maxRetainedChildren) === null ? "" : String(a.maxRetainedChildren),
    cpuBudget: num(a.cpuBudget) === null ? "" : String(a.cpuBudget),
    ramBudgetGiB: num(a.ramBudgetBytes) === null ? "" : String(Math.round((a.ramBudgetBytes / GIB) * 100) / 100),
    storageBudgetGiB: num(a.storageBudgetBytes) === null ? "" : String(Math.round((a.storageBudgetBytes / GIB) * 100) / 100),
    maxChildLifetime: lifetimeTextOf(a.maxChildLifetimeSeconds),
    allowNeverLifetime: tri(a.allowNeverLifetime),
    allowSharing: tri(a.allowSharing),
  };
}

/** "" → null (inherit); "true"/"false" → boolean; else a problem. Pure. */
function parseTri(value, field, problems) {
  const s = str(value).toLowerCase();
  if (!s || s === "inherit") return null;
  if (s === "true" || s === "yes" || s === "on") return true;
  if (s === "false" || s === "no" || s === "off") return false;
  problems.push({ field, reason: "must be true, false or empty (inherit)" });
  return null;
}

/** "" → null; a non-negative integer → number; else a problem. Pure. */
function parseNonNegInt(value, field, problems) {
  const s = str(value);
  if (!s) return null;
  const n = Number(s);
  if (!Number.isInteger(n) || n < 0) { problems.push({ field, reason: "must be a whole number ≥ 0, or empty (inherit)" }); return null; }
  return n;
}

/** "" → null; GiB (decimals allowed) → bytes; else a problem. Pure. */
function parseGiB(value, field, problems) {
  const s = str(value);
  if (!s) return null;
  const n = Number(s);
  if (!Number.isFinite(n) || n < 0) { problems.push({ field, reason: "must be a number of GiB ≥ 0, or empty (inherit)" }); return null; }
  return Math.round(n * GIB);
}

/**
 * The allowance editor → `UserAllowanceRequest` (§8.4): every field nullable, null =
 * inherit the host default. Returns { ok, body, problems: [{ field, reason }] }. Pure.
 */
function parseAllowanceForm(form) {
  const f = form && typeof form === "object" ? form : {};
  const problems = [];
  const body = {
    allowChildCreation: parseTri(f.allowChildCreation, "allowChildCreation", problems),
    maxRetainedChildren: parseNonNegInt(f.maxRetainedChildren, "maxRetainedChildren", problems),
    cpuBudget: parseNonNegInt(f.cpuBudget, "cpuBudget", problems),
    ramBudgetBytes: parseGiB(f.ramBudgetGiB, "ramBudgetGiB", problems),
    storageBudgetBytes: parseGiB(f.storageBudgetGiB, "storageBudgetGiB", problems),
    maxChildLifetimeSeconds: null,
    allowNeverLifetime: parseTri(f.allowNeverLifetime, "allowNeverLifetime", problems),
    allowSharing: parseTri(f.allowSharing, "allowSharing", problems),
  };
  if (str(f.maxChildLifetime)) {
    const lt = parseLifetime(f.maxChildLifetime);
    if (!lt.ok) problems.push({ field: "maxChildLifetime", reason: lt.reason });
    else if (lt.seconds === null) problems.push({ field: "maxChildLifetime", reason: "a maximum cannot be 'never'; leave it empty for no maximum" });
    else body.maxChildLifetimeSeconds = lt.seconds;
  }
  return { ok: problems.length === 0, body, problems };
}

/** The per-VM overrides editor → `VmOverrideRequest` (§8.5, restrict-only subset). Pure. */
function parseOverridesForm(form) {
  const parsed = parseAllowanceForm(form);
  const b = parsed.body;
  const problems = parsed.problems.filter((p) => ["allowChildCreation", "maxRetainedChildren", "maxChildLifetime", "allowNeverLifetime", "allowSharing"].indexOf(p.field) >= 0);
  return {
    ok: problems.length === 0,
    problems,
    body: {
      allowChildCreation: b.allowChildCreation, maxRetainedChildren: b.maxRetainedChildren,
      maxChildLifetimeSeconds: b.maxChildLifetimeSeconds, allowNeverLifetime: b.allowNeverLifetime, allowSharing: b.allowSharing,
    },
  };
}

/** The user editor → `PUT /users/{name}` body (§8.4). Only the fields that were set. Pure. */
function parseUserForm(form) {
  const f = form && typeof form === "object" ? form : {};
  const problems = [];
  const body = {};
  const role = str(f.role).toLowerCase();
  if (role) {
    if (role !== "admin" && role !== "user") problems.push({ field: "role", reason: "must be admin or user" });
    else body.role = role;
  }
  const enabled = parseTri(f.enabled, "enabled", problems);
  if (enabled !== null) body.enabled = enabled;
  const maxVms = parseNonNegInt(f.maxVms, "maxVms", problems);
  if (maxVms !== null) body.maxVms = maxVms;
  const hf = parseTri(f.allowHostForwards, "allowHostForwards", problems);
  if (hf !== null) body.allowHostForwards = hf;
  if (!problems.length && !Object.keys(body).length) problems.push({ field: "", reason: "nothing to change" });
  return { ok: problems.length === 0, body, problems };
}

/** The new-user form → the existing `POST /users` body plus the additive allowance. Pure. */
function parseNewUserForm(form) {
  const f = form && typeof form === "object" ? form : {};
  const problems = [];
  const name = str(f.name);
  if (!name) problems.push({ field: "name", reason: "a user name is required (DOMAIN\\user or a plain name)" });
  const role = str(f.role).toLowerCase() || "user";
  if (role !== "admin" && role !== "user") problems.push({ field: "role", reason: "must be admin or user" });
  const maxVms = parseNonNegInt(f.maxVms, "maxVms", problems);
  const body = { name, role };
  if (maxVms !== null) body.maxVms = maxVms;
  const hf = parseTri(f.allowHostForwards, "allowHostForwards", problems);
  if (hf !== null) body.allowHostForwards = hf;
  return { ok: problems.length === 0, body, problems };
}

/**
 * One config section's edited JSON → its object. A parse failure is reported inline as
 * `{ field: <section>, reason }`; the service's own validation (§8.2 `400 validation
 * { field, reason }`) is reported the same way by configProblemsOf. Pure.
 */
function parseConfigSection(key, text) {
  const k = str(key);
  if (CONFIG_SECTIONS.indexOf(k) < 0) return { ok: false, problems: [{ field: k, reason: "not a configuration section" }] };
  let value;
  try { value = JSON.parse(String(text == null ? "" : text)); } catch (e) {
    return { ok: false, problems: [{ field: k, reason: "not valid JSON: " + errText(e) }] };
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) return { ok: false, problems: [{ field: k, reason: "must be a JSON object" }] };
  return { ok: true, value };
}

/** The `PUT /host/config` body from edited sections (a section is replaced whole). Pure. */
function buildConfigRequest(sections) {
  const problems = [];
  const body = {};
  for (const s of (Array.isArray(sections) ? sections : [])) {
    if (!s || !str(s.key)) continue;
    const parsed = parseConfigSection(s.key, s.text);
    if (!parsed.ok) { problems.push(...parsed.problems); continue; }
    body[str(s.key)] = str(s.expectedUpdatedAt) ? { ...parsed.value, expectedUpdatedAt: str(s.expectedUpdatedAt) } : parsed.value;
  }
  if (!problems.length && !Object.keys(body).length) problems.push({ field: "", reason: "no section was changed" });
  return { ok: problems.length === 0, body, problems };
}

/** The inline problems an API error carries (`validation`, `config-conflict`). Pure. */
function configProblemsOf(error) {
  const b = error && error.body && typeof error.body === "object" ? error.body : {};
  const code = errCode(error);
  if (code === "validation") return [{ field: str(b.field), reason: str(b.reason) || str(b.detail) || "invalid" }];
  if (code === "config-conflict") return [{ field: str(b.field) || "", reason: "somebody else changed this section meanwhile — reload and edit again" }];
  return [{ field: "", reason: errText(error) }];
}

// ── Minimal user view (§10.2) ────────────────────────────────────────────────

/** The child rows under a primary: name, state, lease or overdue, sharing. Pure. */
function childRows(children, now) {
  return (Array.isArray(children) ? children : [])
    .filter((c) => c && typeof c === "object" && str(c.name))
    .map((c) => {
      const lease = c.lease && typeof c.lease === "object" ? c.lease : null;
      const overdue = !!(lease && (lease.overdue === true || str(lease.state).toLowerCase() === "overdue"));
      const sharing = str(c.sharing).toLowerCase();
      const state = str(c.state).toLowerCase() || "unknown";
      const op = operationText(c.currentOperation);
      return {
        name: str(c.name),
        state,
        lease: leaseText(lease, now),
        overdue,
        sharing,
        shared: sharing === "host",
        busy: !!op || c.deleting === true,
        operation: c.deleting === true && !op ? "deleting" : op,
        // Presentation only (§2.2): the two panel actions, offered when the service says
        // the caller may, and greyed while an operation holds the VM.
        canShutdown: !op && c.deleting !== true && (state === "running" || state === "paused") && allows(c, "shutdown"),
        canDelete: !op && c.deleting !== true && allows(c, "delete"),
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));
}

/** Does `allowedActions` list an action? An ABSENT list (an older answer) refuses nothing:
 *  the route re-checks. Pure. */
function allows(vm, action) {
  if (!vm || !Array.isArray(vm.allowedActions)) return true;
  return vm.allowedActions.map(str).indexOf(action) >= 0;
}

/**
 * The panel's `state.children` for the active instance — null HIDES the card (§10.2:
 * "hidden entirely when /health lacks children"; also every local instance). Pure.
 *   { primary, visible, items, problem }
 */
function childrenCardState(input = {}) {
  const backend = str(input.backend).toLowerCase();
  if (backend !== "hyperv-remote") return null;
  if (input.supported !== true) return null;
  const items = Array.isArray(input.items) ? childRows(input.items, input.now) : null;
  return {
    primary: str(input.primary),
    visible: true,
    items: items || [],
    // A read that failed on a host that HAS the feature: say so, keep the last rows.
    problem: items ? "" : (str(input.problem) || "could not read the child VMs"),
  };
}

/**
 * What a `vm-shutdown` job's end means for the user, honestly (§5.5, D1): the graceful
 * shutdown either completed, timed out, was unavailable (no integration services), was
 * superseded, or the job failed. Never "shut down" unless the VM was observed Off. Pure.
 */
function shutdownOutcome(job, name) {
  const j = job && typeof job === "object" ? job : {};
  const state = str(j.state).toLowerCase();
  const result = j.result && typeof j.result === "object" ? j.result : {};
  const outcome = str(result.outcome).toLowerCase();
  const err = str(j.error);
  const who = str(name || result.name) || "the VM";
  if (state === "succeeded" && outcome === "completed") return { ok: true, text: `${who} shut down (guest observed off).` };
  if (state === "succeeded" && outcome === "superseded") return { ok: true, text: `The shutdown of ${who} was superseded by a newer lifecycle action; nothing was changed.` };
  if (err === "guest-shutdown-unavailable" || outcome === "unavailable") {
    return { ok: false, text: `${who} could NOT be shut down gracefully: the guest offers no shutdown integration (no integration services, or the VM is paused). The service does not force it off; delete the VM or shut the guest down from inside it.` };
  }
  if (err === "shutdown-timeout" || outcome === "timeout") {
    return { ok: false, text: `${who} did not power off within the host's graceful-shutdown timeout. The request was accepted by the guest; the service does not force it off.` };
  }
  if (state === "failed") return { ok: false, text: `The shutdown of ${who} failed: ${err || "no reason given"}.` };
  if (state === "cancelled") return { ok: false, text: `The shutdown of ${who} was cancelled.` };
  return { ok: false, text: `The shutdown of ${who} is still ${state || "pending"} (job ${str(j.id) || "?"}).` };
}

/**
 * Poll a job to a terminal state through the client. Returns the last body seen (which
 * may be non-terminal when `attempts` ran out). `opts.sleep` is the injectable wait.
 */
async function awaitJob(client, jobId, opts = {}) {
  const attempts = opts.attempts != null ? opts.attempts : 60;
  const delayMs = opts.delayMs != null ? opts.delayMs : 2000;
  const sleep = opts.sleep || ((ms) => new Promise((r) => setTimeout(r, ms)));
  let last = null;
  for (let i = 0; i < attempts; i++) {
    last = await client.getJob(jobId);
    if (last && TERMINAL_JOB_STATES.indexOf(str(last.state).toLowerCase()) >= 0) return last;
    await sleep(delayMs);
  }
  return last;
}

// ── Confirmations (§10.4) ────────────────────────────────────────────────────

/** Which cascade problem an error is: "required" | "scope-changed" | "expired" | null. Pure. */
function cascadeKindOf(error) {
  const code = errCode(error);
  if (errStatus(error) !== 409) return null;
  if (code === "cascade-confirmation-required") return "required";
  if (code === "cascade-scope-changed") return "scope-changed";
  if (code === "cascade-token-expired") return "expired";
  return null;
}

/**
 * The §10.4 modal, as DATA, from a `409 cascade-confirmation-required` (or
 * `cascade-scope-changed`) problem body. Lists EVERY child, marks the shared ones
 * loudly, and names the permanent disk removal. `requireTypedName` is the existing rule
 * for remote removal. Pure.
 */
function cascadeConfirmation(input = {}) {
  const primary = str(input.primary) || "this VM";
  const body = input.problem && typeof input.problem === "object" ? (input.problem.body && typeof input.problem.body === "object" ? input.problem.body : input.problem) : {};
  const children = (Array.isArray(body.children) ? body.children : []).map((c) => {
    const o = c && typeof c === "object" ? c : { name: c };
    const sharing = str(o.sharing).toLowerCase() || "private";
    return {
      name: str(o.name),
      state: str(o.state).toLowerCase() || "unknown",
      sharing,
      shared: sharing === "host",
      diskGb: num(o.diskGb),
      mediaCount: num(o.mediaCount) || 0,
    };
  });
  const n = children.length;
  const shared = children.filter((c) => c.shared).length;
  const lines = [
    `Deleting "${primary}" also deletes ALL of its ${n} child VM${n === 1 ? "" : "s"}, including shared ones.`,
    "Their virtual disks, saved state and dedicated media are removed permanently.",
    "",
  ];
  for (const c of children) {
    lines.push(
      `  ${c.name}   ${c.state}   ` +
      (c.shared ? "SHARED HOST-WIDE (other users may be using it)" : "private") +
      (c.diskGb !== null ? `   ${c.diskGb} GB disk` : "") +
      (c.mediaCount ? `   ${c.mediaCount} dedicated media` : "")
    );
  }
  lines.push("");
  lines.push(`Type the instance name to confirm.` + (body.expiresAt ? ` (This confirmation expires at ${formatWhen(body.expiresAt)}.)` : ""));
  return {
    kind: str(body.code) === "cascade-scope-changed" ? "scope-changed" : "required",
    title: `Delete "${primary}" and its ${n} child VM${n === 1 ? "" : "s"}?`,
    detail: lines.join("\n"),
    children,
    sharedCount: shared,
    cascadeToken: str(body.cascadeToken),
    expiresAt: str(body.expiresAt),
    requireTypedName: true,
    typedName: primary,
    confirmLabel: n ? `Delete ${primary} and ${n} child VM${n === 1 ? "" : "s"}` : `Delete ${primary}`,
  };
}

/** The smaller child Delete modal (§10.4). Pure. */
function childDeleteConfirmation(child) {
  const c = child && typeof child === "object" ? child : { name: child };
  const name = str(c.name) || "this child VM";
  const sharing = str(c.sharing).toLowerCase();
  const shared = sharing === "host" || c.shared === true;
  return {
    title: `Delete the child VM "${name}"?`,
    detail:
      `${name} is ${shared ? "SHARED HOST-WIDE — other users may be using it" : sharing === "private" ? "private" : "of unknown sharing scope — other users may be using it"}` +
      (str(c.state) ? ` and currently ${str(c.state)}` : "") + ".\n\n" +
      "Its disk, saved state and dedicated media are removed permanently. " +
      "A running VM is powered off for deletion.",
    confirmLabel: `Delete ${name}`,
    shared,
  };
}

// ── Offers (§10.2 host connect / register) ───────────────────────────────────

/**
 * Enrolled hosts with ZERO own VMs on this PC — the "Create first Construct VM here"
 * offer. `hosts` are the globalState enrolment records, `instances` the registry list;
 * URLs are compared through the injected `sameUrl` (remotehost.sameServiceUrl) so
 * "https://h:7462" and "h" are one host. Pure.
 */
function firstVmOffers(input = {}) {
  const hosts = Array.isArray(input.hosts) ? input.hosts.filter((h) => h && str(h.url)) : [];
  const list = Array.isArray(input.instances) ? input.instances : [];
  const same = typeof input.sameUrl === "function" ? input.sameUrl : (a, b) => str(a).toLowerCase() === str(b).toLowerCase();
  return hosts
    .filter((h) => !list.some((i) => i && i.service && str(i.service.url) && same(i.service.url, h.url)))
    .map((h) => ({ url: str(h.url), identity: str(h.identity), role: str(h.role) }));
}

/** Hosts enrolled in the UI or already configured by the command-line VM installer.
 * Explicit enrolments win so their selected credentials and certificate pins survive.
 * A registry entry supplies connection details only; admin authority is still probed. */
function discoverHosts(hosts, instances, sameUrl) {
  const same = typeof sameUrl === "function" ? sameUrl : (a, b) => str(a).toLowerCase() === str(b).toLowerCase();
  const result = (Array.isArray(hosts) ? hosts : []).filter((h) => h && str(h.url));
  for (const instance of Array.isArray(instances) ? instances : []) {
    if (!instance || str(instance.backend).toLowerCase() !== "hyperv-remote") continue;
    const service = instance.service;
    const url = service && str(service.url);
    if (!url || result.some((h) => same(h.url, url))) continue;
    result.push({ url, auth: str(service.auth) || "negotiate" });
  }
  return result;
}

/** The enrolment record for an instance's service URL, or null. Pure. */
function hostEntryFor(instance, hosts, sameUrl) {
  const url = instance && instance.service && str(instance.service.url);
  if (!url) return null;
  const same = typeof sameUrl === "function" ? sameUrl : (a, b) => str(a).toLowerCase() === str(b).toLowerCase();
  return (Array.isArray(hosts) ? hosts : []).find((h) => h && str(h.url) && same(h.url, url)) || null;
}

// ── The model ────────────────────────────────────────────────────────────────

/**
 * One enrolled host's administration state machine. `deps`:
 *   client   a src/remotehost.js client for THIS host (or null: unreachable/no credential)
 *   host     the host name for messages
 *   url      the service URL
 *   backend  "hyperv-remote" (a local backend renders nothing)
 *   now      () => ms (tests)
 *   log      (line) => void — never receives a secret
 *
 * The model is a plain object with a `state` (what the webview renders), `detect()`,
 * `load(tab)` and `perform(action, args)`. Every call re-classifies the host on a
 * refusal (§10.3) and never throws: failures become `state.notice` and a returned
 * `{ ok: false, error }`.
 */
function createHostAdminModel(deps = {}) {
  const client = deps.client || null;
  const host = str(deps.host) || (client && client.host) || "";
  const now = typeof deps.now === "function" ? deps.now : () => Date.now();
  const log = typeof deps.log === "function" ? deps.log : () => {};

  const state = {
    host, url: str(deps.url),
    mode: "unavailable", message: "", retryable: false,
    maintenance: null, features: featureSet(null), identity: null,
    tabs: tabsFor(null), activeTab: "overview",
    busy: false, notice: null,
    overview: null, vms: null, users: null, media: null, operations: null, config: null, maintenanceTab: null,
    lastKnownAt: null,
    problem: str(deps.problem),
  };

  const model = { state };
  const updater = require("./hostupdate").createHostUpdater({ client, now,
    pending: deps.pendingUpdate, save: deps.savePendingUpdate,
    changed: () => {
      state.updatePending = updater.state.pending;
      state.updateChecking = updater.state.checking;
      state.updateError = updater.state.error;
      if (updater.state.status) state.maintenanceTab = toUpdateView(updater.state.status);
      if (model.changed) model.changed();
    },
  });
  state.updatePending = updater.state.pending;
  model.refreshUpdates = async () => {
    if (state.mode !== "admin" || !state.features.updates) return;
    try { await updater.refresh({ checkLatest: !state.maintenance }); }
    catch (e) { refused(e); }
  };

  function setState(next) {
    Object.assign(state, next);
    state.tabs = tabsFor(state);
    return state;
  }

  function notice(level, text) { state.notice = text ? { level, text } : null; }

  /** Re-classify on an error; returns true when the mode changed (the caller stops). */
  function refused(e) {
    const before = state.mode;
    const next = applyRefusal({ mode: state.mode, message: state.message, maintenance: state.maintenance, host }, e, host);
    setState({ mode: next.mode, message: next.message, maintenance: next.maintenance, retryable: !!next.retryable });
    if (next.mode !== before) {
      log(`hostadmin[${host}]: ${before} -> ${next.mode} (${errStatus(e)} ${errCode(e) || ""})`);
      return true;
    }
    return false;
  }

  model.detect = async function detect() {
    state.busy = true;
    try {
      const resolved = await resolveHostState(client, { backend: deps.backend, host, problem: deps.problem });
      setState({
        mode: resolved.mode, message: resolved.message, retryable: !!resolved.retryable,
        maintenance: resolved.maintenance, features: resolved.features, identity: resolved.identity,
      });
      if (resolved.mode === "admin" || resolved.mode === "user") state.lastKnownAt = new Date(now()).toISOString();
      return state;
    } finally { state.busy = false; }
  };

  /** Load one tab's data. Only in `admin` mode; a refusal re-classifies. */
  model.load = async function load(tab) {
    const id = str(tab) || state.activeTab;
    state.activeTab = id;
    if (state.mode !== "admin" || !client) return state;
    const available = state.tabs.find((t) => t.id === id);
    if (!available || !available.available) return state;
    state.busy = true;
    // The notice is NOT cleared by a load: an action's outcome must survive the tab
    // reload that follows it. `perform` clears it when the next action starts.
    try {
      if (id === "overview") {
        state.overview = toOverview(await client.hostStatus());
      } else if (id === "vms") {
        const list = await client.vms({ kind: "all" });
        state.vms = { rows: toVmRows(list, now()), childrenFeature: state.features.children };
      } else if (id === "users") {
        state.users = { rows: (await client.users()).map(toUserRow).sort((a, b) => a.name.localeCompare(b.name)) };
      } else if (id === "media") {
        let catalog = null, catalogProblem = "";
        try { catalog = toIsoCatalogView(await client.isoCatalog()); }
        catch (e) { if (refused(e)) return state; catalogProblem = errText(e); }
        let items = null, mediaProblem = "";
        if (state.features.media) {
          // Omitting owner requests the admin inventory; any supplied owner is literal.
          try { items = (await client.media()).map(toMediaRow); }
          catch (e) { if (refused(e)) return state; mediaProblem = errText(e); }
        } else {
          mediaProblem = "child media is not available on this host version";
        }
        state.media = { catalog, catalogProblem, items: items || [], mediaProblem };
      } else if (id === "operations") {
        const jobs = (await client.jobs({ limit: 100 })).map(toJobRow);
        let audit = [], auditProblem = "";
        try { audit = (await client.audit({ limit: 50 })).map(toAuditRow); }
        catch (e) { if (refused(e)) return state; auditProblem = errText(e); }
        state.operations = { jobs, audit, auditProblem };
      } else if (id === "config") {
        const sections = toConfigView(await client.hostConfig());
        let capabilities = null;
        try { capabilities = toCapabilityRows(await client.hostCapabilities()); } catch (e) { if (refused(e)) return state; }
        state.config = { sections, capabilities, problems: [] };
      } else if (id === "maintenance") {
        await model.refreshUpdates();
      }
      state.lastKnownAt = new Date(now()).toISOString();
    } catch (e) {
      if (!refused(e)) notice("error", errText(e));
    } finally { state.busy = false; }
    return state;
  };

  /**
   * One mutation. Returns { ok, result?, error?, cascade?, secret? }:
   *   cascade   a §10.4 confirmation the adapter must show (delete of a primary)
   *   secret    a ONE-TIME plaintext (token issue/rotation) the adapter shows once and
   *             the model neither stores nor logs
   */
  model.perform = async function perform(action, args = {}) {
    const a = str(action);
    if (!client) return { ok: false, error: state.problem || "no client for this host" };
    if (state.mode !== "admin" && ["shutdownVm", "deleteVm", "cancelJob"].indexOf(a) < 0) {
      notice("error", `not an administrator of ${host}`);
      return { ok: false, error: `not an administrator of ${host}` };
    }
    if (state.maintenance && !["refresh", "updatesApply", "updatesResolve"].includes(a)) {
      const text = `${host} is updating (${state.maintenance.phase}); mutations are disabled until it is back.`;
      notice("error", text);
      return { ok: false, error: text };
    }
    state.busy = true;
    notice(null, "");
    try {
      switch (a) {
        case "updatesUpdate": {
          await updater.start();
          return { ok: true };
        }
        case "refresh": {
          await model.detect();
          if (state.mode === "admin") await model.load(state.activeTab);
          return { ok: true };
        }
        case "capacityRefresh": {
          const cap = await client.hostCapacity(true);
          if (state.overview) state.overview = { ...state.overview, capacity: toCapacityBars(cap && cap.summary), capacityProblems: Array.isArray(cap && cap.problems) ? cap.problems.map(str) : [] };
          return { ok: true };
        }
        case "shutdownVm": {
          const name = str(args.name);
          const res = await client.lifecycle(name, { action: "shutdown" });
          notice("info", `Graceful shutdown of ${name} requested (job ${str(res && res.jobId) || "?"}).`);
          return { ok: true, jobId: str(res && res.jobId) };
        }
        case "deleteVm": {
          const name = str(args.name);
          const body = str(args.cascadeToken) ? { cascade: { token: str(args.cascadeToken) } } : undefined;
          try {
            const res = await client.deleteVm(name, body);
            notice("info", `Deletion of ${name} accepted (job ${str(res && res.jobId) || "?"}${res && res.replayed ? ", already running" : ""}).`);
            return { ok: true, jobId: str(res && res.jobId) };
          } catch (e) {
            const kind = cascadeKindOf(e);
            if (kind === "required" || kind === "scope-changed") {
              return { ok: false, cascade: cascadeConfirmation({ primary: name, problem: e }), error: kind === "scope-changed" ? "the set of children changed since the confirmation; confirm the new list" : "" };
            }
            if (kind === "expired") return { ok: false, error: "the confirmation expired; delete again to get a fresh list", expired: true };
            throw e;
          }
        }
        case "cancelJob": {
          const res = await client.cancelJob(str(args.id));
          notice("info", res && res.cancelled ? `Job ${str(args.id)} cancelled.` : `Job ${str(args.id)} could not be cancelled (already finished?).`);
          return { ok: true, cancelled: !!(res && res.cancelled) };
        }
        case "mediaCleanup": {
          const res = await client.mediaCleanup();
          notice("info", `Media cleanup started (job ${str(res && res.jobId) || "?"}).`);
          return { ok: true, jobId: str(res && res.jobId) };
        }
        case "deleteMedia": {
          const res = await client.deleteMedia(str(args.id));
          notice("info", res && res.jobId ? `Media ${str(args.id)} is held open; cleanup will retry (job ${str(res.jobId)}).` : `Media ${str(args.id)} deleted.`);
          return { ok: true };
        }
        case "updateUser": {
          const parsed = parseUserForm(args.form);
          if (!parsed.ok) return { ok: false, problems: parsed.problems, error: parsed.problems.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("; ") };
          await client.updateUser(str(args.name), parsed.body);
          notice("info", `User ${str(args.name)} updated.`);
          return { ok: true };
        }
        case "createUser": {
          const parsed = parseNewUserForm(args.form);
          if (!parsed.ok) return { ok: false, problems: parsed.problems, error: parsed.problems.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("; ") };
          await client.createUser(parsed.body);
          notice("info", `User ${parsed.body.name} registered.`);
          return { ok: true };
        }
        case "deleteUser": {
          await client.deleteUser(str(args.name));
          notice("info", `User ${str(args.name)} removed.`);
          return { ok: true };
        }
        case "saveAllowance": {
          const parsed = parseAllowanceForm(args.form);
          if (!parsed.ok) return { ok: false, problems: parsed.problems, error: parsed.problems.map((p) => p.field + ": " + p.reason).join("; ") };
          await client.putUserAllowance(str(args.name), parsed.body);
          notice("info", `Allowance of ${str(args.name)} saved.`);
          return { ok: true };
        }
        case "issueToken": {
          const res = await client.issueUserToken(str(args.name), { label: str(args.label) || "issued from VS Code" });
          notice("info", `A token was issued for ${str(args.name)}; it is shown once.`);
          return { ok: true, secret: str(res && (res.token || res.secret || res.plaintext)), label: str(args.label) };
        }
        case "revokeToken": {
          await client.revokeUserToken(str(args.name), str(args.id));
          notice("info", `Token ${str(args.id)} of ${str(args.name)} revoked.`);
          return { ok: true };
        }
        case "listTokens": {
          const list = await client.userTokens(str(args.name));
          return { ok: true, tokens: (Array.isArray(list) ? list : []).map((t) => ({ id: str(t.id), label: str(t.label), created: formatWhen(t.created), lastUsed: t.lastUsed ? formatWhen(t.lastUsed) : "never" })) };
        }
        case "loadOverrides": {
          const res = await client.overrides(str(args.name));
          return { ok: true, stored: allowanceForm(res && res.stored), effective: allowanceText(res && res.effective) };
        }
        case "saveOverrides": {
          const parsed = parseOverridesForm(args.form);
          if (!parsed.ok) return { ok: false, problems: parsed.problems, error: parsed.problems.map((p) => p.field + ": " + p.reason).join("; ") };
          await client.putOverrides(str(args.name), parsed.body);
          notice("info", `Overrides of ${str(args.name)} saved.`);
          return { ok: true };
        }
        case "clearOverrides": {
          await client.deleteOverrides(str(args.name));
          notice("info", `Overrides of ${str(args.name)} cleared.`);
          return { ok: true };
        }
        case "rotateVmToken": {
          const kind = str(args.kind).toLowerCase() === "legacy" ? "legacy" : "primary";
          const res = await client.rotateVmToken(str(args.name), { kind });
          notice("info", `The VM token of ${str(args.name)} was rotated (${kind}); issue and deliver a fresh guest credential with Provision-AgentVM.ps1 -InstanceName ${str(args.name)} -RotateVmToken.`);
          return { ok: true, secret: str(res && res.vmToken), kind };
        }
        case "revokeVmToken": {
          await client.revokeVmToken(str(args.name));
          notice("info", `The VM token of ${str(args.name)} was revoked; restore expose/heartbeat with Provision-AgentVM.ps1 -InstanceName ${str(args.name)} -RotateVmToken.`);
          return { ok: true };
        }
        case "saveConfig": {
          const built = buildConfigRequest(args.sections);
          if (!built.ok) {
            if (state.config) state.config = { ...state.config, problems: built.problems };
            return { ok: false, problems: built.problems, error: built.problems.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("; ") };
          }
          try {
            const res = await client.putHostConfig(built.body);
            state.config = { ...(state.config || {}), sections: toConfigView(res), problems: [] };
            notice("info", "Host configuration saved.");
            return { ok: true };
          } catch (e) {
            if (errStatus(e) === 400 || errCode(e) === "config-conflict") {
              const problems = configProblemsOf(e);
              if (state.config) state.config = { ...state.config, problems };
              return { ok: false, problems, error: problems.map((p) => (p.field ? p.field + ": " : "") + p.reason).join("; ") };
            }
            throw e;
          }
        }
        case "updatesCheck": {
          const res = await client.updatesCheck(str(args.releaseTag) ? { releaseTag: str(args.releaseTag) } : {});
          const text = checkResultText(res);
          state.maintenanceTab = { ...(state.maintenanceTab || toUpdateView(null)), checkResult: text };
          notice("info", text);
          return { ok: true, text };
        }
        case "updatesStage": {
          const res = await client.updatesStage(str(args.releaseTag) ? { releaseTag: str(args.releaseTag) } : {});
          notice("info", `Staging started (update ${str(res && res.updateId) || "?"}, job ${str(res && res.jobId) || "?"}).`);
          return { ok: true, updateId: str(res && res.updateId), jobId: str(res && res.jobId) };
        }
        case "updatesApply": {
          const res = await client.updatesApply({ updateId: str(args.updateId) });
          notice("info", `Apply started (update ${str(args.updateId)}, job ${str(res && res.jobId) || "?"}). The service will drain, hand off and restart.`);
          return { ok: true, jobId: str(res && res.jobId) };
        }
        case "updatesCancel": {
          const res = await client.updatesCancel({ updateId: str(args.updateId) });
          notice("info", `Update ${str(args.updateId)} is now ${str(res && res.state) || "cancelled"}.`);
          return { ok: true };
        }
        case "updatesResolve": {
          const act = str(args.action).toLowerCase();
          if (["commit", "abort", "close"].indexOf(act) < 0) return { ok: false, error: "resolve action must be commit, abort or close" };
          const res = await client.updatesResolve({ updateId: str(args.updateId), action: act });
          notice("info", `Update ${str(args.updateId)} resolved (${act}): ${str(res && res.state) || "resolvedByAdmin"}.`);
          return { ok: true };
        }
        default:
          return { ok: false, error: `unknown host-administration action "${a}"` };
      }
    } catch (e) {
      if (refused(e)) return { ok: false, error: state.message, refused: true };
      notice("error", errText(e));
      return { ok: false, error: errText(e), status: errStatus(e), code: errCode(e) };
    } finally { state.busy = false; }
  };

  return model;
}

module.exports = {
  FEATURE_NAMES, CHILD_ACTIONS, CONFIG_SECTIONS, TABS, MAINTENANCE_POLL_MS, TERMINAL_JOB_STATES, GIB,
  formatBytes, formatWhen, formatDuration, pct,
  featureSet, isMaintenanceError, maintenanceOf, classifyHost, resolveHostState, applyRefusal, tabsFor, pollIntervalMs,
  leaseText, guestText, resourcesText, operationText, toVmRow, toVmRows, toCapacityBars, toOverview,
  allowanceText, toUserRow, toMediaRow, toIsoCatalogView, toJobRow, toAuditRow, toConfigView, toCapabilityRows,
  updateActionsFor, toUpdateView, checkResultText,
  parseLifetime, lifetimeTextOf, allowanceForm, parseAllowanceForm, parseOverridesForm, parseUserForm, parseNewUserForm,
  parseConfigSection, buildConfigRequest, configProblemsOf,
  childRows, childrenCardState, shutdownOutcome, awaitJob,
  cascadeKindOf, cascadeConfirmation, childDeleteConfirmation,
  firstVmOffers, discoverHosts, hostEntryFor,
  resourceUsageView, createHostAdminModel,
};
