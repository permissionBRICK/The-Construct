"use strict";
// Launched by HostAdminEndToEndTests. Every product operation crosses HTTPS; CONTROL
// lines only manipulate test-owned clock/inventory/maintenance/driver fixtures.
const fs = require("node:fs");
const path = require("node:path");
const http = require("node:http");
const { spawn } = require("node:child_process");
const { createHash, X509Certificate } = require("node:crypto");
const readline = require("node:readline");
const rh = require("../../extension/src/remotehost.js");
const base = process.env.E2E_BASE, temp = process.env.E2E_TEMP;
const secrets = [process.env.E2E_ADMIN_TOKEN, "auxiliary-answer-file-secret-e2e"];
const output = [];
let checks = 0;
function check(condition, label) {
  if (!condition) throw new Error(label);
  checks++; console.log("PASS " + label);
}
const input = readline.createInterface({ input: process.stdin });
async function control(action, values = {}) {
  const response = new Promise(resolve => input.once("line", line => resolve(JSON.parse(line))));
  console.log("CONTROL " + JSON.stringify({ action, ...values }));
  return response;
}
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const pin = new X509Certificate(fs.readFileSync(process.env.E2E_CA)).fingerprint256;
const client = token => rh.createClient({ baseUrl: base, auth: { kind: "token", token }, pin });
const admin = client(secrets[0]);
async function api(token, method, route, body, expected = 200) {
  const response = await fetch(base + "/api/v1" + route, {
    method, headers: { Authorization: "VmToken " + token, ...(body ? { "Content-Type": "application/json" } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {}), signal: AbortSignal.timeout(20000),
  });
  const text = await response.text();
  if (expected === 503) check(response.headers.get("retry-after") === "30", "maintenance carries Retry-After header");
  if (response.status !== expected) throw new Error(`${method} ${route.split("?")[0]} expected ${expected}, got ${response.status} (${safeCode(text)})`);
  return text ? JSON.parse(text) : null;
}
function safeCode(text) {
  try { const b = JSON.parse(text); return /^[a-z-]+$/.test(b.code) ? b.code : "uncoded"; } catch { return "non-json"; }
}
async function refused(promise, status, code) {
  let error;
  try { await promise; } catch (e) { error = e; }
  check(error && error.status === status && (!code || error.problem?.code === code || error.code === code), `extension refusal ${status}${code ? " " + code : ""} (got ${error?.status}:${error?.code})`);
  return error.problem || error.body || error;
}
async function finish(c, accepted, state = "succeeded") {
  if (!accepted.jobId) return accepted;
  for (let i = 0; i < 200; i++) {
    const j = await c.getJob(accepted.jobId);
    if (!["queued", "running"].includes(j.state)) {
      if (j.state !== state) throw new Error(`job ${j.kind} expected ${state}, got ${j.state} (${j.error || "no code"})`);
      return j;
    }
    await delay(50);
  }
  throw new Error("job deadline exceeded");
}
let guestToken;
async function cli(args, expected = 0, extra = {}) {
  const tokenPath = path.join(temp, "vm-token");
  fs.writeFileSync(tokenPath, guestToken, { mode: 0o600 });
  const result = await new Promise((resolve, reject) => {
    const p = spawn("bash", ["bin/construct", "vm", ...args], {
      env: { ...process.env, CONSTRUCT_SERVICE_URL: base, CONSTRUCT_INSTANCE_NAME: "alice-primary",
        CONSTRUCT_VM_TOKEN_FILE: tokenPath, CONSTRUCT_SERVICE_CA_FILE: process.env.E2E_CA,
        CONFIG_FILE: path.join(temp, "empty-config"), CONSTRUCT_SERVICE_AUTH_SCHEME: "VmToken",
        CONSTRUCT_VM_POLL_INTERVAL_SEC: "0.05", ...extra }, timeout: 30000,
    });
    let stdout = "", stderr = "";
    p.stdout.on("data", b => stdout += b); p.stderr.on("data", b => stderr += b);
    p.on("error", reject); p.on("close", code => resolve({ code, stdout, stderr }));
  });
  output.push(result.stdout, result.stderr);
  if (result.code !== expected) throw new Error(`CLI ${args[0]} expected exit ${expected}, got ${result.code}`);
  return expected === 0 && args.includes("--json") ? JSON.parse(result.stdout) : result;
}
const iso = Buffer.alloc(40960); iso.set([1, 67, 68, 48, 48, 49, 1], 32768);
const aux = Buffer.from(iso); aux.write(secrets[1], 100);
fs.writeFileSync(path.join(temp, "install.iso"), iso);
fs.writeFileSync(path.join(temp, "aux.iso"), aux, { mode: 0o600 });
let requests = 0;
const server = http.createServer((req, res) => {
  if (req.url !== "/fixture/install.iso") { res.writeHead(404); res.end(); return; }
  requests++; res.writeHead(200, { "Content-Length": iso.length, "Content-Type": "application/octet-stream" }); res.end(iso);
});
async function main() {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const mediaUrl = `http://127.0.0.1:${server.address().port}/fixture/install.iso`;
  check((await admin.health()).apiFeatures.includes("children"), "real HTTPS service advertises children");
  for (const name of ["alice", "bob"]) {
    await admin.createUser({ name, role: "user", maxVms: 2, allowHostForwards: true,
      allowance: { allowChildCreation: true, maxRetainedChildren: 8, allowSharing: true, allowNeverLifetime: true } });
  }
  const aliceToken = (await admin.issueUserToken("alice", { label: "e2e" })).token;
  const bobToken = (await admin.issueUserToken("bob", { label: "e2e" })).token;
  secrets.push(aliceToken, bobToken);
  const alice = client(aliceToken), bob = client(bobToken);
  check((await alice.whoami()).effective.maxRetainedChildren === 8, "admin enrollment and allowance reach ordinary user");
  await refused(bob.hostConfig(), 403);
  const primary = await finish(alice, await alice.createVm({ name: "alice-primary", cpu: 1, ramGb: 1, diskGb: 8 }));
  const oldToken = primary.result.vmToken; secrets.push(oldToken);
  check(!!oldToken && !(await alice.getJob(primary.id)).result.vmToken, "primary provisioning token is delivered once");
  guestToken = (await alice.rotateVmToken("alice-primary")).vmToken; secrets.push(guestToken);
  await api(oldToken, "GET", "/vms/alice-primary/identity", null, 401);
  check((await cli(["identity", "--json"])).tokenKind === "primary", "rotated primary token works through CLI with trusted CA");
  const badCa = await cli(["identity", "--json"], 8, { CONSTRUCT_SERVICE_CA_FILE: path.join(temp, "missing-ca.pem") });
  check(badCa.stderr.includes("cannot reach the host service"), "CLI rejects an unavailable CA instead of bypassing TLS verification");
  const bobPrimary = await finish(bob, await bob.createVm({ name: "bob-primary", cpu: 1, ramGb: 1, diskGb: 8 }));
  const bobVmToken = bobPrimary.result.vmToken; secrets.push(bobVmToken);
  const createArgs = ["--cpus", "1", "--ram-mb", "1024", "--disk-gb", "1", "--lifetime", "10m", "--json"];
  await cli(["create", "--name", "url-child", "--iso-url", mediaUrl, "--sha256", createHash("sha256").update(iso).digest("hex"), ...createArgs]);
  check(requests === 1 && (await alice.getState("url-child")).state === "running", "CLI URL child downloads bytes from local HTTP path and boots");
  const uploadResult = await cli(["create", "--name", "upload-child", "--iso", path.join(temp, "install.iso"), "--aux-iso", path.join(temp, "aux.iso"), ...createArgs]);
  check(!uploadResult.job.result.vmToken, "child creation job issues no token");
  await refused(alice.rotateVmToken("upload-child"), 409);
  const uploaded = await alice.getVm("upload-child");
  check(uploaded.media.length === 2 && uploaded.media.some(m => m.role === "auxiliary"), "CLI uploads install and auxiliary ISO and attaches both");
  check(!uploaded.guest.constructCommit && !uploaded.guest.provisionedAt, "child boot does not claim guest provisioning");
  check((await cli(["list", "--json"])).length === 2, "CLI lists both children");
  const media = await alice.media(); const install = media.find(m => m.source === "url");
  check(!!install, "URL media registry persists source");
  await refused(alice.deleteMedia(install.id), 409, "media-in-use");
  // Shared caller operations remain charged to Alice, with owner-only metadata withheld.
  await cli(["share", "upload-child", "--scope", "host", "--json"]);
  check((await bob.sharedVms()).some(v => v.name === "upload-child"), "extension discovers host-shared child");
  await api(bobVmToken, "GET", "/vms/upload-child", null, 200);
  await api(bobVmToken, "GET", "/vms/url-child", null, 403);
  for (const [method, route, body] of [
    ["DELETE", "/vms/upload-child", null], ["PUT", "/vms/upload-child/sharing", { scope: "private" }],
    ["PUT", "/vms/upload-child/hardware", { ramMb: 2048 }], ["PUT", "/vms/upload-child/media", { installMediaId: null }],
    ["POST", "/vms/upload-child/lease", { lifetime: "1h" }],
  ]) await api(bobVmToken, method, route, body, 403);
  check(true, "shared primary can inspect shared child but cannot inspect private sibling or change ownership controls");
  const sharedAux = await bob.mediaItem(uploaded.media.find(m => m.role === "auxiliary").id);
  check(!sharedAux.sha256 && !sharedAux.owner && !sharedAux.sourceUrl, "shared auxiliary metadata hides owner, checksum and source");
  const session = await api(bobVmToken, "POST", "/vms/upload-child/console/sessions", {}, 201);
  const screenshot = await fetch(base + `/api/v1/vms/upload-child/console/sessions/${session.sessionId}/screenshot`, {
    headers: { Authorization: "VmToken " + bobVmToken }, signal: AbortSignal.timeout(20000),
  });
  check(screenshot.status === 200 && Buffer.from(await screenshot.arrayBuffer()).subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])), "shared caller gets fake console PNG over HTTPS");
  await api(bobVmToken, "POST", `/vms/upload-child/console/sessions/${session.sessionId}/keyboard`, { kind: "key", keyCode: 13 }, 200);
  const sharedForward = await api(bobVmToken, "POST", "/vms/upload-child/forwards", { vmPort: 8082, target: "client" }, 201);
  check(sharedForward.destination.via === "bob-primary" && sharedForward.destination.requestedBy === "vm:bob-primary", "shared client forward uses requester's primary");
  check((await bob.forwardsVia("bob-primary")).some(f => f.id === sharedForward.id), "extension polls child forward through requester's primary");
  await cli(["share", "upload-child", "--scope", "private", "--json"]);
  await api(bobVmToken, "POST", `/vms/upload-child/console/sessions/${session.sessionId}/keyboard`, { kind: "key", keyCode: 13 }, 403);
  check(!(await bob.forwardsVia("bob-primary")).some(f => f.id === sharedForward.id), "making child private revokes shared forward and console access");
  await cli(["share", "upload-child", "--scope", "host", "--json"]);
  const leaseBefore = (await alice.getVm("upload-child")).lease;
  await finish(bob, await bob.lifecycle("upload-child", { action: "restart" }));
  check((await alice.getVm("upload-child")).lease.expiresAt === leaseBefore.expiresAt, "shared restart retains lease deadline");
  await bob.lifecycle("upload-child", { action: "save" });
  check((await bob.getState("upload-child")).state === "saved", "shared save reaches Saved");
  await bob.lifecycle("upload-child", { action: "start", lifetime: "15m" });
  check((await bob.getState("upload-child")).state === "running", "shared start resumes with explicit lifetime");
  await finish(bob, await bob.lifecycle("upload-child", { action: "shutdown" }));
  check((await bob.getState("upload-child")).state === "off", "shared graceful shutdown reaches Off");
  // Two requests race for exactly the last GiB, using real SQLite admission.
  await cli(["shutdown", "url-child", "--json"]);
  await control("capacity", { totalGb: 3 });
  await admin.putHostConfig({ capacity: { mode: "enforce", ramHeadroomBytes: 0, storageHeadroomBytes: 0,
    cpuBudget: null, maxVcpusPerVm: 8, reconcileSeconds: 3600, orphanReservationTimeoutSeconds: 600 } });
  const before = await admin.hostCapacity(false);
  check(before.summary.complete, "capacity inventory complete: " + before.problems.join(","));
  check(before.summary.ram.availableBytes === 1073741824, "capacity fixture leaves exactly one GiB");
  const race = await Promise.allSettled([alice.lifecycle("url-child", { action: "start", lifetime: "5m" }), bob.lifecycle("upload-child", { action: "start", lifetime: "5m" })]);
  check(race.filter(r => r.status === "fulfilled").length === 1, `concurrent last-GiB starts admit exactly one (${race.map(r => r.status === "fulfilled" ? "accepted" : r.reason.status + ":" + r.reason.code).join(",")})`);
  const loser = race.find(r => r.status === "rejected").reason;
  check(loser.status === 409 && (loser.code === "capacity-exhausted" || loser.problem?.code === "capacity-exhausted"), "capacity loser gets structured conflict");
  const held = await admin.hostCapacity(false);
  check(held.summary.ram.reservedBytes === 3 * 1073741824, "SQLite retains exactly three GiB after contention");
  await admin.putHostConfig({ capacity: { mode: "observe", ramHeadroomBytes: 0, storageHeadroomBytes: 0,
    reconcileSeconds: 3600, orphanReservationTimeoutSeconds: 600 } });
  const winner = race[0].status === "fulfilled" ? "url-child" : "upload-child";
  await control("shutdownUnavailable");
  const failed = await finish(alice, await alice.lifecycle(winner, { action: "shutdown" }), "failed");
  check(failed.error.includes("guest-shutdown-unavailable") && (await alice.getState(winner)).state === "running", "unavailable graceful shutdown fails without save or force-off");
  const expiry = await control("expire", { seconds: 301 });
  check(expiry.jobs.length === 1, "fake clock selects only the running child's due lease");
  await finish(alice, { jobId: expiry.jobs[0] }, "failed");
  check((await alice.getVm(winner)).lease.overdue && (await alice.getState(winner)).state === "running", "failed lease expiry remains overdue and running");
  check((await control("expire", { seconds: 1 })).jobs.length === 0, "overdue expiry respects retry interval");
  await control("shutdownAvailable");
  const retry = await control("expire", { seconds: 600 });
  const retries = await Promise.all(retry.jobs.map(jobId => finish(alice, { jobId })));
  check(retries.some(job => job.vmName === winner), "overdue expiry retries after fake clock advances");
  check((await alice.getState(winner)).state === "off" && (await alice.children("alice-primary")).length === 2,
    "lease expiry gracefully shuts down and retains both children");
  // Host-wide disabled applies both to the child and to an attempted parent relay.
  await admin.putHostConfig({ network: { hostForwardsEnabled: false, directAddressReporting: true } });
  for (const name of ["alice-primary", "upload-child"])
    await api(guestToken, "POST", `/vms/${name}/forwards`, { vmPort: 8080, target: "host" }, 403);
  const deniedForward = await cli(["forward", "upload-child", "8080", "--to", "host", "--json"], 4);
  check(deniedForward.stderr.includes("host-forwards-disabled"), "disabled host forwards cannot be bypassed through the parent or CLI");
  // Exercise the same gate used by host-update's drain, without launching any updater.
  await control("drain");
  const maintenance = await api(guestToken, "POST", "/vms/alice-primary/children", { name: "drain-child", cpus: 1, ramMb: 512, diskGb: 1, lifetime: "5m", media: { installMediaId: install.id } }, 503);
  check(maintenance.code === "maintenance" && maintenance.phase === "draining", "drain gate returns maintenance problem");
  await control("freeze");
  // Cross the real recovery timer tick; the fixture holds the updater acceptance lock.
  await delay(2100);
  await api(guestToken, "PUT", "/vms/url-child/sharing", { scope: "host" }, 503);
  await cli(["share", "url-child", "--scope", "host", "--json"], 10);
  check((await alice.health()).status === "maintenance", "health remains readable during drain");
  await control("reopen");
  await cli(["share", "url-child", "--scope", "host", "--json"]);
  check((await alice.health()).status === "ok", "reopened gate accepts mutations again");
  // Legacy compatibility: original four routes, plus explicitly additive discovery/report routes.
  const legacy = (await bob.rotateVmToken("bob-primary", { kind: "legacy" })).vmToken; secrets.push(legacy);
  const retainedGuestToken = guestToken; guestToken = legacy;
  await cli(["list", "--json"], 9, { CONSTRUCT_INSTANCE_NAME: "bob-primary" }); guestToken = retainedGuestToken;
  check(true, "CLI legacy token gets upgrade-required exit 9");
  const forward = await api(legacy, "POST", "/vms/bob-primary/forwards", { vmPort: 8081, target: "client" }, 201);
  check(!("destination" in forward), "legacy primary forward retains flat wire shape");
  await api(legacy, "GET", "/vms/bob-primary/forwards");
  await api(legacy, "DELETE", `/vms/bob-primary/forwards/${forward.id}`, null, 204);
  await api(legacy, "POST", "/vms/bob-primary/activity", { busy: true, reasons: ["e2e"] }, 204);
  check(true, "legacy token retains all four original routes");
  for (const [method, route, body] of [["GET", "/vms", null], ["GET", "/media", null], ["GET", "/users", null],
    ["POST", "/vms/bob-primary/children", { cpus: 1, ramMb: 512, diskGb: 1, lifetime: "5m", media: { installMediaId: install.id } }],
    ["POST", "/vms/bob-primary/power", { action: "start" }], ["GET", "/vms/upload-child", null]])
    await api(legacy, method, route, body, 403);
  check((await api(legacy, "GET", "/vms/bob-primary/identity")).delegation === null, "legacy discovery exposes no delegation");
  // Confirmation is invalidated by a child created between preview and acceptance.
  const preview = await refused(alice.deleteVm("alice-primary"), 409, "cascade-confirmation-required");
  check(preview.children.length === 2 && preview.children.some(c => c.sharing === "host"), "cascade preview lists shared children");
  await cli(["create", "--name", "late-child", "--media", install.id, "--no-start", ...createArgs]);
  const changed = await refused(alice.deleteVm("alice-primary", { cascade: { token: preview.cascadeToken } }), 409, "cascade-scope-changed");
  check(changed.children.length === 3 && (await alice.children("alice-primary")).length === 3, "stale confirmation cannot delete newly added child");
  await finish(alice, await alice.deleteVm("alice-primary", { cascade: { token: changed.cascadeToken } }));
  check(!(await alice.listVms()).some(v => v.name === "alice-primary" || v.parent === "alice-primary"), "fresh cascade deletes parent and every private/shared child");
  await api(guestToken, "GET", "/vms/alice-primary/identity", null, 401);
  for (const m of await alice.media()) await alice.deleteMedia(m.id);
  check((await alice.media()).length === 0, "cascade releases media references for cleanup");
  const audit = await admin.audit({ limit: 5000 });
  const actions = new Set(audit.map(e => e.action));
  for (const action of ["user.create", "token.issue", "vm.create", "vm.token.rotate", "media.acquire", "media.upload.begin",
    "media.upload.chunk", "media.upload.complete", "child.create", "vm.lifecycle", "vm.share", "host.config", "forward.add", "forward.remove", "media.delete"])
    check(actions.has(action), "audit contains " + action);
  check(!actions.has("vm.activity"), "successful heartbeats do not fill the audit trail");
  check(audit.some(e => e.actor === "vm:alice-primary" && e.detail?.includes("owner=alice")), "delegated audit retains initiator and effective owner");
  output.push(JSON.stringify(audit), JSON.stringify(await admin.jobs({ limit: 200 })));
  check(secrets.every(s => s && output.every(text => !text.includes(s))), "CLI output, persisted jobs and audit contain no credential or auxiliary sentinel");
  fs.writeFileSync(path.join(temp, "secrets"), secrets.join("\n"), { mode: 0o600 });
  console.log(`HOST_ADMIN_E2E ${checks} passed, 0 failed`);
}
main().catch(error => { console.error("FAIL " + error.message); process.exitCode = 1; }).finally(() => {
  server.closeAllConnections(); server.close(); input.close();
});
