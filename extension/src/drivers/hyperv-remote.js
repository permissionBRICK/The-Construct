"use strict";
// Hypervisor driver: REMOTE Hyper-V via the `constructd` host service (backend
// "hyperv-remote"). The extension-side half of docs/drivers.md §4; the PowerShell half
// is drivers/hyperv-remote/HyperVRemote.Driver.ps1.
//
// WHERE THIS RUNS — like the local driver, this is part of the UI extension, so its
// Node code runs on the user's LOCAL Windows host even when the window is attached to
// the VM over Remote-SSH. Unlike the local driver it spawns no elevated console and
// raises no UAC prompt: every operation is an HTTPS call the SERVICE performs.
//
// The client itself (credential providers, certificate pinning, error mapping) is
// src/remotehost.js; this file is only the contract mapping, so it stays small enough
// to read as a table.

const remotehost = require("../remotehost");

/**
 * What this backend can do.
 *
 *   checkpoints: false     the service exposes no checkpoint operations — and
 *                          Set-AgentVmCheckpoints.ps1 drives the LOCAL Hyper-V, so
 *                          running it for a remote instance would reconfigure (and
 *                          delete checkpoints on) a local VM that merely shares the
 *                          name. drivers/index.js gates `setCheckpoints` on this flag.
 *   console: "none"        there is no vmconnect to a machine you are not sitting at.
 *   suspend: true          the service's idle policy saves VMs; a start resumes them.
 *   hostLifecycle: true    Auto-Install.ps1 gained a remote path in B7, so the host's
 *                          own scripts really can create and delete this backend's VMs
 *                          (`-Backend hyperv-remote -ServiceUrl … -InstanceName …`).
 *                          That is what re-enables Reinstall/Redownload in the panel.
 */
const CAPABILITIES = { checkpoints: false, console: "none", suspend: true, hostLifecycle: true, children: false };

/**
 * `children: false` above is the STATIC answer — the host-administration contract §10.1
 * resolves it lazily: a service whose `/health` lists the `children` feature has child
 * VMs, an older one has not. `capabilitiesFor(instance, opts)` is that resolution, and
 * `queryFeatures` is the cached probe behind it (one `GET /health` per host per
 * FEATURE_TTL_MS; a failed probe is not cached, so a host that comes back is asked again).
 */
const FEATURE_TTL_MS = 60000;
const featureCache = new Map();

/** The service URL recorded on an instance, or "" when the entry carries none (which
 *  makes the instance unusable — every call below reports that, once, rather than
 *  guessing a host). Pure. */
function serviceUrlOf(instance) {
  const svc = instance && instance.service;
  return (svc && typeof svc.url === "string" && svc.url.trim()) ? svc.url.trim() : "";
}

/** The VM's name on the host service. Both registry readers PIN `vmName` to the instance
 *  name for this backend (instances.remoteIdentityProblems: a rebuild reaches the service
 *  by `-InstanceName <name>` and deletes what it finds there, so an entry naming a
 *  different service VM would split power and rebuild across two machines) — so the two
 *  are the same string here, and the fallback only covers an object built by hand. Pure. */
function vmNameOf(instance) {
  return (instance && (instance.vmName || instance.name)) || "";
}

/**
 * A client for an instance, plus the reason there is none.
 *
 *   { client, problem }   exactly one of the two is set.
 *
 * `opts` carries the test seams (`fetchImpl`, `spawnImpl`, `env`, `remoteLib`, `pin`)
 * and the credential (`opts.auth`). The credential is the EXTENSION LAYER's to supply:
 * a token lives in VS Code SecretStorage and this module never touches vscode, and the
 * Negotiate provider needs the path of lib/AgentVm.Remote.ps1 (`opts.remoteLib`) because
 * Node has no SSPI.
 *
 * A token instance with NO token is a problem, not an invitation to try something else:
 * quietly falling back to the Windows identity would ask the service a question it has
 * already been told the answer to, and would report "unknown" for what is really "your
 * stored token is gone" — or, worse, succeed as a different identity.
 */
function resolveClient(instance, opts = {}) {
  const url = serviceUrlOf(instance);
  const name = (instance && instance.name) || "?";
  if (!url) {
    return { client: null, problem: `the instance "${name}" has no host service recorded (service.url), so it cannot be reached from here.` };
  }
  const svc = (instance && instance.service) || {};
  const auth = opts.auth || { kind: svc.auth === "token" ? "token" : "negotiate" };
  if (auth.kind === "token" && !auth.token) {
    return { client: null, problem: `the API token for ${url} is not available (it is stored in VS Code's SecretStorage). Run "The Construct: Add Remote Host" to re-enter it.` };
  }
  if (auth.kind !== "token" && !opts.remoteLib) {
    return { client: null, problem: `signing in to ${url} with your Windows account needs lib/AgentVm.Remote.ps1 from the installed Construct scripts, which was not found on this PC.` };
  }
  try {
    return {
      client: remotehost.createClient({
        baseUrl: url,
        auth,
        pin: opts.pin,
        remoteLib: opts.remoteLib,
        fetchImpl: opts.fetchImpl,
        spawnImpl: opts.spawnImpl,
        env: opts.env,
        log: opts.log,
        timeoutMs: opts.timeoutMs,
      }),
      problem: "",
    };
  } catch (e) {
    // A malformed service URL, or a transport the client refuses (plain http to a
    // non-loopback host). A registry problem the reader also reports; here it must not
    // throw — the contract says a driver never rejects.
    return { client: null, problem: `${url} cannot be used: ${e && e.message ? e.message : e}` };
  }
}

/** Backwards-compatible shape: the client, or null. */
function clientFor(instance, opts = {}) {
  return resolveClient(instance, opts).client;
}

/**
 * The VM's power state: 'running' | 'off' | 'absent' | 'unknown'. Never rejects.
 *
 * Only a 404 is `absent`. An unreachable service, a refused credential or a timeout is
 * `unknown` — "can't tell" — because the panel offers to CREATE a VM for `absent`, and
 * a flaky network must never trigger that.
 */
async function queryVmState(instance, opts = {}) {
  const { client, problem } = resolveClient(instance, opts);
  if (!client) {
    (opts.log || (() => {}))(`hyperv-remote: state probe skipped — ${problem}`);
    return "unknown";
  }
  try {
    const res = await client.getState(vmNameOf(instance));
    return remotehost.mapVmState(res && res.state);
  } catch (e) {
    if (e && e.status === 404) return "absent";
    const log = opts.log || (() => {});
    log(`hyperv-remote: state probe failed — ${e && e.message ? e.message : e}`);
    return "unknown";
  }
}

/**
 * Automatic checkpoints are not a capability of this backend, so there is nothing to
 * probe and no call is made. `unsupported` is the contract's value for "this backend
 * has no such feature at all" — the panel hides the affordance rather than showing an
 * unknown state it will keep re-probing.
 */
function queryAutoCheckpoints() {
  return Promise.resolve("unsupported");
}

/**
 * Start the VM: POST /vms/{name}/power {"action":"start"} — which also RESUMES a VM the
 * idle policy saved. No elevation and no console: the service does it.
 *
 * Fire-and-forget like the local driver's elevated Start-VM (the caller polls SSH
 * reachability and opens the VM once it answers), so this returns whether the request
 * was ISSUED, not whether the VM is up. It never rejects; a failure is logged.
 */
function startVm(instance, opts = {}) {
  const log = (opts && opts._log) || opts.log || console.warn;
  const { client, problem } = resolveClient(instance, opts);
  if (!client) {
    log(`Construct: cannot start "${(instance && instance.name) || "?"}" — ${problem}`);
    return false;
  }
  const name = vmNameOf(instance);
  Promise.resolve()
    .then(() => client.power(name, "start"))
    .catch((e) => log(`Construct: couldn't start "${name}" on ${client.host}: ${e && e.message ? e.message : e}`));
  return true;
}

/**
 * The service's `apiFeatures` (host-administration contract §8.1): an array, or null when
 * it could not be established — an unreachable host, a refused credential, or an OLD
 * service, which answers `/health` with 404 (`{ features: null, old: true }`). Never
 * rejects. Cached per service URL for FEATURE_TTL_MS on success only; `opts.now` and
 * `opts.noCache` are the test seams.
 */
async function queryFeatures(instance, opts = {}) {
  const url = serviceUrlOf(instance);
  const now = typeof opts.now === "function" ? opts.now() : Date.now();
  const key = url.toLowerCase();
  const cached = !opts.noCache && featureCache.get(key);
  if (cached && now - cached.at < FEATURE_TTL_MS) return cached.value;
  const { client, problem } = resolveClient(instance, opts);
  const log = opts.log || (() => {});
  if (!client) {
    log(`hyperv-remote: feature probe skipped — ${problem}`);
    return { features: null, old: false, problem };
  }
  try {
    const health = await client.health();
    const features = health && Array.isArray(health.apiFeatures) ? health.apiFeatures.map(String) : [];
    const value = { features, old: false, problem: "", status: health && health.status === "maintenance" ? "maintenance" : "ok" };
    featureCache.set(key, { at: now, value });
    return value;
  } catch (e) {
    // 404: the service predates `/health` — an OLD service, which is an answer, not a
    // failure, and is cached like one (it will not grow the route until it is updated).
    if (e && e.status === 404) {
      const value = { features: null, old: true, problem: "" };
      featureCache.set(key, { at: now, value });
      return value;
    }
    log(`hyperv-remote: feature probe failed — ${e && e.message ? e.message : e}`);
    return { features: null, old: false, problem: (e && e.message) || String(e) };
  }
}

/** Forget every cached feature answer (tests, and "Add Remote Host" re-enrolment). */
function resetFeatureCache() { featureCache.clear(); }

/**
 * The capability table with `children` RESOLVED for this instance's host: true only when
 * `/health` lists the `children` feature. Everything else is the static table. Never
 * rejects.
 */
async function capabilitiesFor(instance, opts = {}) {
  const probe = await queryFeatures(instance, opts);
  const features = probe && Array.isArray(probe.features) ? probe.features : [];
  return { ...CAPABILITIES, children: features.indexOf("children") >= 0 };
}

/**
 * This primary's children plus accessible host-shared guests, for the panel's
 * user view. Separate routes also support hosts with the older owned-only list.
 * Never rejects:
 *
 *   { supported: false, items: [], problem }   the host lacks the feature (or is old,
 *                                              unreachable, or the credential is gone)
 *   { supported: true,  items: [...], problem: "" }  the rows, possibly none
 *   { supported: true,  items: null, problem }  the feature exists but this read failed
 *
 * `supported: false` is what HIDES the panel's Child VMs card entirely (§10.2), which is
 * why the feature probe comes first: an older service must not be asked a route it does
 * not have, and a failed read must not be mistaken for "no children".
 */
async function queryChildren(instance, opts = {}) {
  const probe = await queryFeatures(instance, opts);
  if (!probe || !Array.isArray(probe.features)) {
    return { supported: false, items: [], problem: (probe && probe.problem) || (probe && probe.old ? "this host's service predates child VMs" : "the host could not be reached") };
  }
  if (probe.features.indexOf("children") < 0) return { supported: false, items: [], problem: "this host's service has no child VMs" };
  const { client, problem } = resolveClient(instance, opts);
  if (!client) return { supported: false, items: [], problem };
  try {
    const [list, shared] = await Promise.all([client.children(vmNameOf(instance)), client.sharedVms()]);
    const rows = new Map();
    for (const vm of [...(Array.isArray(list) ? list : []), ...(Array.isArray(shared) ? shared : [])]) {
      const key = String(vm.name).toLowerCase();
      if (!rows.has(key)) rows.set(key, vm);
    }
    return { supported: true, items: [...rows.values()], problem: "" };
  } catch (e) {
    (opts.log || (() => {}))(`hyperv-remote: children read failed — ${e && e.message ? e.message : e}`);
    return { supported: true, items: null, problem: (e && e.message) || String(e), status: e && e.status };
  }
}

module.exports = {
  backend: "hyperv-remote",
  capabilities: CAPABILITIES,
  FEATURE_TTL_MS,
  serviceUrlOf, vmNameOf, clientFor, resolveClient,
  queryVmState, queryAutoCheckpoints, startVm,
  queryFeatures, resetFeatureCache, capabilitiesFor, queryChildren,
};
