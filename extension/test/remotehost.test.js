"use strict";
// Plain-node unit tests for the remote-host client (src/remotehost.js) and the
// hyperv-remote driver (src/drivers/hyperv-remote.js).
//
// Nothing here opens a socket or spawns a process: the HTTP layer is injected
// (`fetchImpl`), the PowerShell delegate is injected (`spawnImpl`), and the TLS probe is
// injected (`tlsConnect`). That is the point of the §4.8 module rules — the client's
// core has no vscode API and no owned transport, so it is testable as pure code.
// Run: node remotehost.test.js
const { EventEmitter } = require("events");
const os = require("os");
const fs = require("fs");
const path = require("path");
const rh = require("../src/remotehost");
const remoteDriver = require("../src/drivers/hyperv-remote");
const drivers = require("../src/drivers");

let pass = 0, fail = 0;
function ok(name, cond, detail) {
  if (cond) { pass++; console.log("  PASS  " + name); }
  else { fail++; console.log("  FAIL  " + name + (detail ? "   << " + detail : "")); }
}
const eq = (name, actual, expected) =>
  ok(name, actual === expected, `expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);

const SVC = "https://buildbox.example.local:7462";
const FP_A = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
const FP_B = "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00";

/** A fake HTTP layer: scripted answers, and it records every request it was handed. */
function fakeHttp(script, sink) {
  let i = 0;
  return function (url, init) {
    const step = Array.isArray(script) ? script[Math.min(i++, script.length - 1)] : script;
    if (sink) sink.calls.push({ url, init });
    if (step.throws) return Promise.reject(new Error(step.throws));
    return Promise.resolve({ status: step.status, text: step.text == null ? "" : step.text });
  };
}
const newSink = () => ({ calls: [] });

/** A throwaway self-signed certificate for the real-TLS tests, or null when openssl
 *  isn't available. Returns { key, cert, fingerprint } with the fingerprint in the
 *  canonical spelling. */
function makeTestCertificate() {
  const cp = require("child_process");
  const crypto = require("crypto");
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-tls-"));
  try {
    const r = cp.spawnSync("openssl", [
      "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
      "-subj", "/CN=localhost",
      "-keyout", path.join(dir, "key.pem"), "-out", path.join(dir, "cert.pem"),
    ], { stdio: "ignore" });
    if (r.status !== 0) return null;
    const cert = fs.readFileSync(path.join(dir, "cert.pem"));
    return {
      key: fs.readFileSync(path.join(dir, "key.pem")),
      cert,
      fingerprint: rh.formatFingerprint(new crypto.X509Certificate(cert).fingerprint256),
    };
  } catch (_) {
    return null;
  } finally {
    try { fs.rmSync(dir, { recursive: true, force: true }); } catch (_) {}
  }
}

console.log("\n=== service URLs ===");
eq("url: a bare name gets https and the service port", rh.normalizeServiceUrl("buildbox"), "https://buildbox:7462");
eq("url: an explicit port is kept", rh.normalizeServiceUrl("buildbox:9000"), "https://buildbox:9000");
eq("url: a trailing path/slash is dropped", rh.normalizeServiceUrl("https://buildbox.local:7462/api/"), "https://buildbox.local:7462");
eq("url: http stays http (development/fake services)", rh.normalizeServiceUrl("http://127.0.0.1:7999"), "http://127.0.0.1:7999");
eq("url: whitespace is trimmed", rh.normalizeServiceUrl("  buildbox:7462  "), "https://buildbox:7462");
eq("url: an IPv6 literal keeps its brackets", rh.normalizeServiceUrl("https://[fe80::1]:7462"), "https://[fe80::1]:7462");
ok("url: an empty value throws", (() => { try { rh.normalizeServiceUrl("  "); return false; } catch (_) { return true; } })());
ok("url: a non-http scheme throws", (() => { try { rh.normalizeServiceUrl("ftp://x"); return false; } catch (_) { return true; } })());
// The PowerShell client derives the SAME slug — the two share one pin file, so a host
// added in VS Code is already trusted when Auto-Install.ps1 runs in a console.
eq("slug: host_port, lowercased", rh.hostSlug(SVC), "buildbox.example.local_7462");
eq("slug: an IPv6 literal is sanitised to file-name-safe characters",
  rh.hostSlug("https://[fe80::1]:7462"), "fe80__1_7462");
eq("slug: the same host in another spelling is the same key",
  rh.hostSlug("BUILDBOX.example.local:7462"), rh.hostSlug(SVC));
eq("secret key: one SecretStorage entry per host",
  rh.tokenSecretKey(SVC), "construct.remote.token:buildbox.example.local_7462");

console.log("\n=== fingerprints ===");
eq("fp: colon-separated uppercase is canonical", rh.formatFingerprint(FP_A.toLowerCase()), FP_A);
eq("fp: an unseparated spelling normalises to the same value",
  rh.formatFingerprint(FP_A.replace(/:/g, "")), FP_A);
eq("fp: a spaced spelling normalises too", rh.formatFingerprint(FP_A.replace(/:/g, " ")), FP_A);
eq("fp: something that isn't 64 hex digits is rejected", rh.formatFingerprint("AB:CD"), "");
eq("fp: an empty value is rejected", rh.formatFingerprint(""), "");
ok("fp: match is spelling-insensitive", rh.fingerprintsMatch(FP_A, FP_A.toLowerCase().replace(/:/g, "")));
ok("fp: a different certificate does NOT match", !rh.fingerprintsMatch(FP_A, FP_B));
ok("fp: an empty pin never matches", !rh.fingerprintsMatch("", FP_A) && !rh.fingerprintsMatch(FP_A, ""));

// The pin file is the CONTRACT shared with lib/AgentVm.Remote.ps1: same directory, same
// file name, same content. Round-trip it through a fake %LOCALAPPDATA%.
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "construct-remote-"));
const env = { LOCALAPPDATA: tmp };
eq("pin: path is <LOCALAPPDATA>\\The-Construct\\remote\\<slug>.pin",
  rh.pinPath(SVC, env), path.join(tmp, "The-Construct", "remote", "buildbox.example.local_7462.pin"));
eq("pin: unknown host reads back empty", rh.readPin(SVC, { env }), "");
rh.writePin(SVC, FP_A.toLowerCase(), { env });
eq("pin: round-trips in the canonical spelling", rh.readPin(SVC, { env }), FP_A);
eq("pin: stored as plain text (a hash is not a secret)",
  fs.readFileSync(rh.pinPath(SVC, env), "utf8"), FP_A);
ok("pin: a malformed fingerprint is refused",
  (() => { try { rh.writePin(SVC, "nope", { env }); return false; } catch (_) { return true; } })());

// fetchFingerprint reads whatever the host presents, trusting nothing.
(async () => {
  const fakeTls = (opts, cb) => {
    const s = new EventEmitter();
    s.destroy = () => {};
    s.getPeerCertificate = () => ({ fingerprint256: FP_B });
    setImmediate(cb);
    return s;
  };
  eq("fingerprint: read from the presented certificate",
    await rh.fetchFingerprint(SVC, { tlsConnect: fakeTls }), FP_B);
  eq("fingerprint: http has no certificate to read",
    await rh.fetchFingerprint("http://127.0.0.1:7999", { tlsConnect: fakeTls }), "");

  console.log("\n=== paths and error mapping ===");
  eq("path: a bare route gets the api prefix", rh.apiPath("whoami"), "/api/v1/whoami");
  eq("path: a rooted route gets it too", rh.apiPath("/vms/x/state"), "/api/v1/vms/x/state");
  eq("path: an already-prefixed route is left alone", rh.apiPath("/api/v1/whoami"), "/api/v1/whoami");
  ok("error: 401 says the credentials were rejected", /rejected these credentials/.test(rh.mapError(401, null)));
  ok("error: 403 explains enrolment/ownership", /not enrolled|not your VM/.test(rh.mapError(403, null)));
  ok("error: 409 is 'not right now' (an endpoint with no forward yet)", /cannot do that right now/.test(rh.mapError(409, null)));
  ok("error: 0 is a transport failure", /Could not reach/.test(rh.mapError(0, null)));
  ok("error: a problem document's title+detail come through",
    rh.mapError(400, { title: "Bad request", detail: "'cpu' must be between 1 and 64." })
      .includes("Bad request — 'cpu' must be between 1 and 64."));

  console.log("\n=== the client: auth header shapes ===");
  const sink = newSink();
  const tokenClient = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "s3cret" }, pin: FP_A,
    fetchImpl: fakeHttp({ status: 200, text: JSON.stringify({ name: "DOMAIN\\alice", known: true, role: "user", maxVms: 2 }) }, sink),
  });
  const me = await tokenClient.whoami();
  eq("token: the identity comes back parsed", me.name, "DOMAIN\\alice");
  eq("token: exactly one request", sink.calls.length, 1);
  eq("token: it hits /api/v1/whoami on the normalised base", sink.calls[0].url, SVC + "/api/v1/whoami");
  eq("token: Authorization is a bearer token", sink.calls[0].init.headers.Authorization, "Bearer s3cret");
  eq("token: the pin is handed to the transport", sink.calls[0].init.pin, FP_A);
  ok("token: a GET carries no body", sink.calls[0].init.body == null);

  const sink2 = newSink();
  const c2 = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "s3cret" }, pin: FP_A,
    fetchImpl: fakeHttp({ status: 202, text: '{"jobId":"abc"}' }, sink2),
  });
  const accepted = await c2.createVm({ name: "work-vm", cpu: 4, ramGb: 8, diskGb: 50 });
  eq("create: the 202 body is parsed", accepted.jobId, "abc");
  eq("create: POSTed as JSON", sink2.calls[0].init.headers["Content-Type"], "application/json");
  eq("create: the body is the spec", sink2.calls[0].init.body, '{"name":"work-vm","cpu":4,"ramGb":8,"diskGb":50}');
  eq("create: the method is POST", sink2.calls[0].init.method, "POST");

  // No pin, no call — the fail-closed rule, mirrored from the PowerShell client.
  const unpinned = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "x" }, pin: "",
    fetchImpl: fakeHttp({ status: 200, text: "{}" }, newSink()),
  });
  let refused = null;
  try { await unpinned.whoami(); } catch (e) { refused = e; }
  ok("pinning: an unpinned https host is refused before the request", !!refused && /has not been confirmed/.test(refused.message));
  eq("pinning: the refusal is a transport failure, not an HTTP status", refused.status, 0);

  // …but a local http development service has no certificate to pin at all.
  const sinkHttp = newSink();
  const devClient = rh.createClient({
    baseUrl: "http://127.0.0.1:7999", auth: { kind: "token", token: "dev" }, pin: "",
    fetchImpl: fakeHttp({ status: 200, text: "{}" }, sinkHttp),
  });
  await devClient.whoami();
  eq("pinning: http is exempt (dev/fake services)", sinkHttp.calls.length, 1);

  console.log("\n=== the client: error mapping ===");
  const errClient = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "x" }, pin: FP_A,
    fetchImpl: fakeHttp({ status: 401, text: '{"title":"Unauthorized","detail":"no."}' }, newSink()),
  });
  let e401 = null;
  try { await errClient.whoami(); } catch (e) { e401 = e; }
  eq("error: the status rides on the error", e401.status, 401);
  ok("error: ...and the message is the mapped one", /rejected these credentials/.test(e401.message));

  const notFound = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "x" }, pin: FP_A,
    fetchImpl: fakeHttp({ status: 404, text: '{"title":"Not found"}' }, newSink()),
  });
  let e404 = null;
  try { await notFound.getState("nope"); } catch (e) { e404 = e; }
  eq("error: 404 keeps its status for the driver's absent mapping", e404.status, 404);

  const dead = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "x" }, pin: FP_A,
    fetchImpl: fakeHttp({ throws: "ECONNREFUSED" }, newSink()),
  });
  let eNet = null;
  try { await dead.whoami(); } catch (e) { eNet = e; }
  eq("error: a transport failure is status 0", eNet.status, 0);

  console.log("\n=== the endpoint reading (plan §4.12) ===");
  // GET /vms/{name}/endpoint and a creation job's `endpoint` have the same shape, and
  // both go through readEndpoint — mirrored by ConvertFrom-ConstructVmEndpoint in
  // lib/AgentVm.Remote.ps1, which runs the same matrix in test/remote-client.test.ps1.
  eq("endpoint: sshHost, sshPort and publicHost are read",
    JSON.stringify(rh.readEndpoint({ sshHost: "buildbox.local", sshPort: 2201, publicHost: "work-vm.vpn.example" })),
    JSON.stringify({ sshHost: "buildbox.local", sshPort: 2201, publicHost: "work-vm.vpn.example" }));
  // A service with no PublicHostPattern (or an older build) states nothing, and the
  // answer is the SSH host — which is what "no pattern configured" means there.
  eq("endpoint: a missing publicHost falls back to the ssh host",
    rh.readEndpoint({ sshHost: "buildbox.local", sshPort: 2201 }).publicHost, "buildbox.local");
  eq("endpoint: a blank publicHost falls back too",
    rh.readEndpoint({ sshHost: "buildbox.local", sshPort: 2201, publicHost: "  " }).publicHost, "buildbox.local");
  eq("endpoint: a missing sshPort is 22", rh.readEndpoint({ sshHost: "b" }).sshPort, 22);
  eq("endpoint: an out-of-range sshPort is 22, not a nonsense dial",
    rh.readEndpoint({ sshHost: "b", sshPort: 99999 }).sshPort, 22);
  eq("endpoint: no ssh host at all is null (there is nothing to dial)",
    rh.readEndpoint({ sshPort: 2201 }), null);
  eq("endpoint: a non-object is null", rh.readEndpoint("nope"), null);

  const sinkEp = newSink();
  const epClient = rh.createClient({
    baseUrl: SVC, auth: { kind: "token", token: "x" }, pin: FP_A,
    fetchImpl: fakeHttp({ status: 200, text: '{"sshHost":"buildbox.local","sshPort":2201,"publicHost":"work-vm.vpn.example"}' }, sinkEp),
  });
  const endpoint = await epClient.getEndpoint("work-vm");
  eq("endpoint: getEndpoint returns the normalised shape", endpoint.publicHost, "work-vm.vpn.example");
  eq("endpoint: ...and still dials the ssh host", endpoint.sshHost, "buildbox.local");
  eq("endpoint: it hits the endpoint route", sinkEp.calls[0].url, SVC + "/api/v1/vms/work-vm/endpoint");

  console.log("\n=== the PowerShell delegate (Negotiate / credentials) ===");
  const script = rh.buildDelegateScript({
    remoteLib: "C:\\scripts\\lib\\AgentVm.Remote.ps1", baseUrl: SVC,
    method: "GET", route: "/api/v1/whoami", bodyJson: null, kind: "negotiate",
  });
  ok("delegate: it dot-sources the shared client", script.includes(". 'C:\\scripts\\lib\\AgentVm.Remote.ps1'"));
  ok("delegate: negotiate uses the process identity", script.includes("New-ConstructApiAuth -Mode negotiate"));
  ok("delegate: it prints one parseable envelope", script.includes("'CONSTRUCT_API '"));
  ok("delegate: it never throws on an HTTP failure (-NoThrow)", script.includes("-NoThrow"));

  const credScript = rh.buildDelegateScript({
    remoteLib: "L.ps1", baseUrl: SVC, method: "GET", route: "/api/v1/whoami",
    bodyJson: null, kind: "credential", user: "DOMAIN\\alice", password: "hunter2",
  });
  ok("delegate: a credential is built from a user name", credScript.includes("'DOMAIN\\alice'"));
  ok("delegate: the PASSWORD is read from stdin, never embedded",
    credScript.includes("[Console]::In.ReadLine()") && !credScript.includes("hunter2"));

  const launch = rh.buildDelegateLaunch({
    remoteLib: "L.ps1", baseUrl: SVC, method: "GET", route: "/api/v1/whoami", bodyJson: null, kind: "negotiate",
  });
  eq("delegate: launched as powershell.exe", launch.file, "powershell.exe");
  ok("delegate: passed as -EncodedCommand (no quoting layer can mangle it)",
    launch.spawnArgs.includes("-EncodedCommand") &&
    Buffer.from(launch.spawnArgs[launch.spawnArgs.length - 1], "base64").toString("utf16le") === launch.command);
  ok("delegate: the password never reaches the command line",
    !rh.buildDelegateLaunch({
      remoteLib: "L.ps1", baseUrl: SVC, method: "GET", route: "/x", bodyJson: null,
      kind: "credential", user: "u", password: "hunter2",
    }).spawnArgs.join(" ").includes(Buffer.from("hunter2", "utf16le").toString("base64")));

  eq("delegate: the envelope is parsed out of noisy stdout",
    rh.parseDelegateOutput('WARNING: something\nCONSTRUCT_API {"status":200,"error":"","body":{"name":"x"}}\n').body.name, "x");
  eq("delegate: no envelope -> null", rh.parseDelegateOutput("nothing here"), null);

  // A delegated call resolves the same { status, text } shape the HTTP layer does, so
  // the request path above it is identical for all three providers.
  const fakeSpawn = (behavior) => function () {
    const child = new EventEmitter();
    child.stdout = new EventEmitter();
    child.stderr = new EventEmitter();
    child.stdin = { write() {}, end() {} };
    child.kill = () => {};
    setImmediate(() => {
      if (behavior.stdout) child.stdout.emit("data", Buffer.from(behavior.stdout));
      if (behavior.stderr) child.stderr.emit("data", Buffer.from(behavior.stderr));
      child.emit("close", 0);
    });
    return child;
  };
  const negClient = rh.createClient({
    baseUrl: SVC, auth: { kind: "negotiate" }, pin: FP_A, remoteLib: "L.ps1",
    spawnImpl: fakeSpawn({ stdout: 'CONSTRUCT_API {"status":200,"error":"","body":{"name":"DOMAIN\\\\bob","known":true}}\n' }),
  });
  eq("negotiate: the delegated identity comes back", (await negClient.whoami()).name, "DOMAIN\\bob");

  const negDenied = rh.createClient({
    baseUrl: SVC, auth: { kind: "negotiate" }, pin: FP_A, remoteLib: "L.ps1",
    spawnImpl: fakeSpawn({ stdout: 'CONSTRUCT_API {"status":401,"error":"refused","body":null}\n' }),
  });
  let negErr = null;
  try { await negDenied.whoami(); } catch (e) { negErr = e; }
  eq("negotiate: a 401 keeps its status (the enrolment flow branches on it)", negErr.status, 401);
  ok("negotiate: ...and carries the helper's own message", /refused/.test(negErr.message));

  const noHelper = rh.createClient({ baseUrl: SVC, auth: { kind: "negotiate" }, pin: FP_A });
  let noHelperErr = null;
  try { await noHelper.whoami(); } catch (e) { noHelperErr = e; }
  ok("negotiate: without lib/AgentVm.Remote.ps1 it says so", /AgentVm.Remote.ps1/.test(noHelperErr.message));

  // ── Plain http is a LOOPBACK-only concession ───────────────────────────────
  // Without TLS there is nothing to encrypt the bearer token with and no certificate to
  // pin, so http is refused for anything but a service on this machine — and refused
  // when the client is BUILT, so no caller can be holding one for such a host.
  console.log("\n=== transport safety (http is loopback-only) ===");
  const buildable = (url) => {
    try { rh.createClient({ baseUrl: url, auth: { kind: "token", token: "t" }, pin: FP_A }); return ""; }
    catch (e) { return e.message; }
  };
  eq("http: 127.0.0.1 is allowed (this is what the fake service listens on)", buildable("http://127.0.0.1:7999"), "");
  eq("http: localhost is allowed", buildable("http://localhost:7999"), "");
  eq("http: ::1 is allowed", buildable("http://[::1]:7999"), "");
  ok("http: a LAN host is refused", /Refusing to talk/.test(buildable("http://buildbox.example.local:7462")));
  ok("http: ...saying the credential would cross the network unencrypted",
    /unencrypted/.test(buildable("http://buildbox.example.local:7462")));
  ok("http: a LAN IP is refused too", /Refusing to talk/.test(buildable("http://10.0.0.5:7462")));
  eq("http: https to the same host is fine", buildable(SVC), "");
  ok("loopback: 127.x is loopback", rh.isLoopbackHost("127.5.5.5") === true);
  ok("loopback: a public address is not", rh.isLoopbackHost("10.0.0.5") === false);
  ok("loopback: a name that merely CONTAINS localhost is not",
    rh.isLoopbackHost("localhost.evil.example") === false);
  // The driver must report it, not throw: a registry entry with an http URL is a
  // configuration problem, and the contract says a driver never rejects.
  const httpInst = {
    name: "work-vm", backend: "hyperv-remote", vmName: "work-vm",
    service: { url: "http://buildbox.example.local:7462", auth: "token" },
  };
  eq("http: the driver reports 'can't tell' for such an entry",
    await remoteDriver.queryVmState(httpInst, { auth: { kind: "token", token: "t" } }), "unknown");
  ok("http: ...with the refusal as the reason",
    /Refusing to talk/.test(remoteDriver.resolveClient(httpInst, { auth: { kind: "token", token: "t" } }).problem));

  // ── REAL TLS: the pin is the identity check, so it has to hold on a socket ──
  // Everything above injects the HTTP layer, which proves what the client ASKS for. This
  // proves what the DEFAULT transport does, because that is where pinning can be
  // silently inert: with `rejectUnauthorized: false` (unavoidable for a self-signed
  // certificate) Node never calls `checkServerIdentity`, so a pin checked there is not
  // checked at all — the request succeeds and the bearer token is delivered to whoever
  // presented the certificate.
  //
  // Skips cleanly (with a reason) when openssl is unavailable to make a test certificate.
  console.log("\n=== real TLS: the certificate pin on a live socket ===");
  const tlsFix = makeTestCertificate();
  if (!tlsFix) {
    console.log("  SKIP  real-TLS pinning — openssl is not available to generate a test certificate");
  } else {
    const https = require("https");
    let sawAuth = null;
    const server = https.createServer({ key: tlsFix.key, cert: tlsFix.cert }, (req, res) => {
      sawAuth = req.headers.authorization || "";
      res.setHeader("content-type", "application/json");
      res.end('{"name":"e2e","known":true}');
    });
    await new Promise((r) => server.listen(0, "127.0.0.1", r));
    const url = `https://127.0.0.1:${server.address().port}/api/v1/whoami`;
    const call = async (pin) => {
      sawAuth = null;
      try {
        const res = await rh.nodeHttp(url, { method: "GET", headers: { Authorization: "Bearer SUPER-SECRET" }, pin });
        return { ok: true, status: res.status };
      } catch (e) { return { ok: false, message: e.message }; }
    };

    const wrong = await call(FP_B);
    ok("tls: a MISMATCHED pin fails the request", wrong.ok === false, wrong.message);
    ok("tls: ...naming it as a fingerprint mismatch", !wrong.ok && /fingerprint mismatch/i.test(wrong.message));
    // The assertion the whole finding turns on.
    ok("tls: ...and the Authorization header never reaches the server", sawAuth === null);

    const unpinned = await call("");
    ok("tls: an UNPINNED host fails the request", unpinned.ok === false);
    ok("tls: ...telling the user to add the host once", !unpinned.ok && /has not been confirmed/i.test(unpinned.message));
    ok("tls: ...and again sends no credential", sawAuth === null);

    const right = await call(tlsFix.fingerprint);
    ok("tls: the MATCHING pin is accepted", right.ok === true && right.status === 200, right.message);
    eq("tls: ...and only then is the credential delivered", sawAuth, "Bearer SUPER-SECRET");

    // A self-signed certificate for an IP literal: SNI must be omitted (TLS forbids an
    // IP ServerName and Node throws instead of eliding it), which the three calls above
    // already exercised — assert the helper that decides it, so the reason is recorded.
    eq("tls: an IPv4 literal gets no SNI name", rh.sniName("127.0.0.1"), "");
    eq("tls: an IPv6 literal gets no SNI name", rh.sniName("[fe80::1]"), "");
    eq("tls: a host NAME does", rh.sniName("buildbox.example.local"), "buildbox.example.local");

    await new Promise((r) => server.close(r));
  }

  console.log("\n=== the hyperv-remote driver ===");
  const INST = {
    name: "work-vm", backend: "hyperv-remote", vmName: "work-vm",
    vmHost: "buildbox.example.local", sshPort: 2201, hostAlias: "work-vm",
    keyName: "construct_work-vm_ed25519", configBranch: "vm-work-vm",
    service: { url: SVC, auth: "token" },
  };
  const drive = (script, sink) => ({
    auth: { kind: "token", token: "t" }, pin: FP_A, fetchImpl: fakeHttp(script, sink || newSink()),
  });

  eq("driver: running -> running", await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"running"}' })), "running");
  eq("driver: off -> off", await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"off"}' })), "off");
  eq("driver: saved -> off (a start resumes it)", await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"saved"}' })), "off");
  eq("driver: paused -> off", await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"paused"}' })), "off");
  eq("driver: a 404 is the ONLY absent", await remoteDriver.queryVmState(INST, drive({ status: 404, text: "{}" })), "absent");
  eq("driver: a 401 is 'can't tell', NOT absent", await remoteDriver.queryVmState(INST, drive({ status: 401, text: "{}" })), "unknown");
  eq("driver: a 403 is 'can't tell', NOT absent", await remoteDriver.queryVmState(INST, drive({ status: 403, text: "{}" })), "unknown");
  eq("driver: an unreachable service is 'can't tell'", await remoteDriver.queryVmState(INST, drive({ throws: "ECONNREFUSED" })), "unknown");
  eq("driver: an unrecognised state is 'can't tell'", await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"starting"}' })), "unknown");
  eq("driver: an entry with no service.url can't be probed",
    await remoteDriver.queryVmState({ ...INST, service: null }, drive({ status: 200, text: '{"state":"running"}' })), "unknown");

  const stateSink = newSink();
  await remoteDriver.queryVmState(INST, drive({ status: 200, text: '{"state":"running"}' }, stateSink));
  eq("driver: the state route is the VM's own", stateSink.calls[0].url, SVC + "/api/v1/vms/work-vm/state");

  eq("driver: checkpoints are unsupported, not probed", await remoteDriver.queryAutoCheckpoints(INST, {}), "unsupported");
  ok("driver: capabilities — no checkpoints, no console, suspend yes, hostLifecycle yes",
    remoteDriver.capabilities.checkpoints === false && remoteDriver.capabilities.console === "none" &&
    remoteDriver.capabilities.suspend === true && remoteDriver.capabilities.hostLifecycle === true);
  eq("driver: registered under its backend id", drivers.getDriver("hyperv-remote"), remoteDriver);

  const startSink = newSink();
  const started = remoteDriver.startVm(INST, drive({ status: 200, text: '{"state":"running"}' }, startSink));
  ok("driver: startVm reports that the request was issued", started === true);
  await new Promise((r) => setImmediate(r));
  eq("driver: ...as POST /power", startSink.calls[0].url, SVC + "/api/v1/vms/work-vm/power");
  eq("driver: ...with action start", startSink.calls[0].init.body, '{"action":"start"}');
  const logged = [];
  ok("driver: startVm without a service URL declines and says why",
    remoteDriver.startVm({ ...INST, service: null }, { _log: (m) => logged.push(m) }) === false &&
    logged.length === 1 && /host service/.test(logged[0]));

  eq("driver: state mapping is the shared helper", rh.mapVmState("SAVED"), "off");
  eq("driver: an empty state is 'can't tell'", rh.mapVmState(""), "unknown");

  // ── The credential the driver CANNOT get for itself ────────────────────────
  // The driver never touches vscode, so the token (SecretStorage) and the path of
  // lib/AgentVm.Remote.ps1 arrive in `opts` from the extension layer. Missing either is
  // a PROBLEM, not a licence to try something else.
  console.log("\n=== the driver's credential requirements ===");
  const noToken = remoteDriver.resolveClient(INST, { auth: { kind: "token", token: "" } });
  ok("cred: a token instance with no token yields no client", noToken.client === null);
  ok("cred: ...and says the token is missing, not something vaguer", /token/i.test(noToken.problem));
  // The regression: silently becoming Negotiate would ask the service a different
  // question and report the answer as if it were about the token.
  ok("cred: ...and does NOT fall back to the Windows identity",
    !/negotiate|windows account/i.test(noToken.problem));
  const negInst = { ...INST, service: { url: SVC, auth: "negotiate" } };
  const noLib = remoteDriver.resolveClient(negInst, {});
  ok("cred: a Negotiate instance with no PowerShell helper yields no client", noLib.client === null);
  ok("cred: ...and names AgentVm.Remote.ps1", /AgentVm\.Remote\.ps1/.test(noLib.problem));
  ok("cred: a Negotiate instance WITH the helper yields a client",
    remoteDriver.resolveClient(negInst, { remoteLib: "C:\\scripts\\lib\\AgentVm.Remote.ps1" }).client !== null);
  const credLog = [];
  eq("cred: a missing token reads as 'can't tell', never 'absent'",
    await remoteDriver.queryVmState(INST, { auth: { kind: "token", token: "" }, log: (m) => credLog.push(m) }), "unknown");
  ok("cred: ...and the reason is logged rather than swallowed", credLog.length === 1 && /token/i.test(credLog[0]));
  const startLog = [];
  ok("cred: startVm with no token declines instead of reporting success",
    remoteDriver.startVm(INST, { auth: { kind: "token", token: "" }, _log: (m) => startLog.push(m) }) === false &&
    startLog.length === 1 && /token/i.test(startLog[0]));

  // ── extension.js's own wiring ─────────────────────────────────────────────
  // extension.js cannot be required under plain node (it needs `vscode`), so its
  // credential resolver is lifted out of the source and EXERCISED with stubs — the same
  // pattern test/vmpower.test.js uses for the checkpoint wiring. This is the production
  // path: without it the driver is called with no credential at all.
  console.log("\n=== extension.js credential wiring ===");
  const extSrc = fs.readFileSync(path.join(__dirname, "..", "extension.js"), "utf8");
  const optsStart = extSrc.indexOf("async function driverOpts(inst)");
  const optsEnd = extSrc.indexOf("\n}", optsStart) + 2;
  ok("wiring: extension.js defines driverOpts", optsStart > 0);
  const secrets = { "construct.remote.token:buildbox.example.local_7462": "stored-token" };
  const stubbed = new Function(
    "remotehost", "extensionContext", "remoteLibPath", "logLine",
    extSrc.slice(optsStart, optsEnd) + "\nreturn driverOpts;"
  )(rh, { secrets: { get: async (k) => secrets[k] } }, () => "C:\\scripts\\lib\\AgentVm.Remote.ps1", () => {});

  const localOpts = await stubbed({ name: "agent-vm", backend: "hyperv-local" });
  eq("wiring: a LOCAL instance gets no extra options at all (zero-change)", JSON.stringify(localOpts), "{}");
  const tokenOpts = await stubbed(INST);
  eq("wiring: a token instance gets the token from SecretStorage", tokenOpts.auth.token, "stored-token");
  eq("wiring: ...under the token provider", tokenOpts.auth.kind, "token");
  ok("wiring: ...plus the PowerShell helper the other providers need", !!tokenOpts.remoteLib);
  const negOpts = await stubbed(negInst);
  eq("wiring: a negotiate instance gets the Windows-identity provider", negOpts.auth.kind, "negotiate");
  // A vanished secret must arrive as an EMPTY token (which the driver refuses), not as a
  // silent switch to another credential.
  const goneOpts = await stubbed({ ...INST, service: { url: "https://other.example:7462", auth: "token" } });
  eq("wiring: a vanished token stays a token credential, empty", goneOpts.auth.kind + ":" + goneOpts.auth.token, "token:");
  // ...and the call sites really pass it on.
  const stateFn = extSrc.slice(extSrc.indexOf("async function withVmState"), extSrc.indexOf("async function driverOpts"));
  ok("wiring: withVmState spreads driverOpts into the state probe",
    /queryVmState\(\{\s*instance: target,\s*\.\.\.\(await driverOpts\(target\)\)/.test(stateFn));
  const startFn = extSrc.slice(extSrc.indexOf("async function runStartAndConnect"), extSrc.indexOf("const startOpts") + 400);
  ok("wiring: runStartAndConnect resolves the credential before starting",
    /const startOpts = await driverOpts\(startInstance\)/.test(startFn));
  ok("wiring: ...and passes it to startVm", /startVm\(\{[^}]*\.\.\.startOpts/.test(startFn));

  try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (_) {}
  console.log(`\n  remote-host client tests — ${pass}/${pass + fail} passed`);
  if (fail) process.exitCode = 1;
})().catch((e) => { console.error(e); process.exitCode = 1; });
