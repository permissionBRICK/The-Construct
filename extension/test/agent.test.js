"use strict";
// Finding, starting and installing the device agent (src/agent.js). Run: node --test extension/test/
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const agent = require("../src/agent");

const TOKEN = "t".repeat(43);

function tempHome(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "construct-ext-"));
  fs.chmodSync(dir, 0o700);
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function installVersion(home, version, exe = "construct-agent") {
  const dir = path.join(home, "versions", version);
  fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  fs.writeFileSync(path.join(dir, exe), "#!/bin/sh\n", { mode: 0o700 });
  fs.chmodSync(path.join(home, "versions"), 0o700);
  fs.writeFileSync(path.join(home, "install.json"), JSON.stringify({ current: version, previous: null, pending: false }));
  return path.join(dir, exe);
}

test("the agent's directory follows AgentOptions.DefaultDataDirectory", () => {
  assert.equal(agent.agentHome({ CONSTRUCT_AGENT_HOME: "/x" }, "linux", "/home/u"), "/x");
  assert.equal(agent.agentHome({ LOCALAPPDATA: "C:\\Users\\u\\AppData\\Local" }, "win32", "C:\\Users\\u"), "C:\\Users\\u\\AppData\\Local\\Construct\\agent");
  assert.equal(agent.agentHome({ XDG_DATA_HOME: "/d" }, "linux", "/home/u"), "/d/construct-agent");
  assert.equal(agent.agentHome({}, "linux", "/home/u"), "/home/u/.local/share/construct-agent");
});

test("the token file needs a port and a token", () => {
  assert.deepEqual(agent.parseEndpoint({ port: 5000, token: TOKEN, pid: 7, startedAt: "2026-01-01T00:00:00Z" }),
    { port: 5000, token: TOKEN, pid: 7, startedAt: "2026-01-01T00:00:00Z" });
  for (const bad of [null, {}, { port: 0, token: TOKEN }, { port: 70000, token: TOKEN }, { port: 5000, token: "short" }, { port: 5000, token: "has space in it 1234567" }, { port: "5000", token: TOKEN }]) {
    assert.equal(agent.parseEndpoint(bad), null, JSON.stringify(bad));
  }
});

test("a health answer counts only for the agent the token file names", () => {
  const e = { port: 1, token: TOKEN, pid: 42, startedAt: "2026-01-01T00:00:00Z" };
  assert.ok(agent.healthMatches({ status: "ok", pid: 42, startedAt: "2026-01-01T00:00:00.000+00:00" }, e));
  assert.ok(!agent.healthMatches({ status: "ok", pid: 43, startedAt: e.startedAt }, e), "another pid");
  assert.ok(!agent.healthMatches({ status: "ok", pid: 42, startedAt: "2026-01-02T00:00:00Z" }, e), "another start");
  assert.ok(!agent.healthMatches({ status: "starting", pid: 42 }, e));
  assert.ok(!agent.healthMatches(null, e));
});

test("the installed executable comes from install.json's current version only", t => {
  const home = tempHome(t);
  assert.equal(agent.installedExecutable(home, "linux"), null);
  const exe = installVersion(home, "1.2.0+abc");
  assert.equal(agent.installedExecutable(home, "linux"), exe);
  fs.writeFileSync(path.join(home, "install.json"), JSON.stringify({ current: "../../etc" }));
  assert.equal(agent.installedExecutable(home, "linux"), null, "a path in current is refused");
  fs.writeFileSync(path.join(home, "install.json"), JSON.stringify({ current: "9.9.9" }));
  assert.equal(agent.installedExecutable(home, "linux"), null, "a version that is not unpacked");
});

test("find: running, stopped or missing", async t => {
  const home = tempHome(t);
  assert.equal((await agent.find({ home, platform: "linux" })).state, "missing");
  installVersion(home, "1.0.0");
  assert.equal((await agent.find({ home, platform: "linux" })).state, "stopped");
  fs.writeFileSync(path.join(home, "endpoint.json"), JSON.stringify({ version: 1, port: 5555, token: TOKEN, pid: 9, startedAt: "2026-01-01T00:00:00Z" }));
  const seen = [];
  const answering = async (endpoint, method, route, opts) => { seen.push({ method, route, opts }); return { status: 200, body: { status: "ok", pid: 9, startedAt: "2026-01-01T00:00:00Z" } }; };
  const found = await agent.find({ home, platform: "linux", request: answering });
  assert.equal(found.state, "running");
  assert.equal(found.endpoint.token, TOKEN);
  assert.equal(seen[0].route, "/api/health");
  assert.equal(seen[0].opts.token, undefined, "the health check carries no token");
  const impostor = async () => ({ status: 200, body: { status: "ok", pid: 10, startedAt: "2026-01-01T00:00:00Z" } });
  assert.equal((await agent.find({ home, platform: "linux", request: impostor })).state, "stopped", "another process on the port");
  const refused = async () => { throw new Error("ECONNREFUSED"); };
  assert.equal((await agent.find({ home, platform: "linux", request: refused })).state, "stopped");
});

test("request: loopback only, the token in the header, no redirect followed", async t => {
  const got = [];
  const server = http.createServer((req, res) => {
    got.push({ url: req.url, auth: req.headers.authorization, host: req.headers.host, body: "" });
    let body = "";
    req.on("data", c => { body += c; });
    req.on("end", () => {
      got[got.length - 1].body = body;
      if (req.url === "/moved") { res.writeHead(302, { Location: "http://example.invalid/" }); return res.end(); }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ ok: true }));
    });
  });
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  t.after(() => server.close());
  const endpoint = { port: server.address().port };
  const r = await agent.request(endpoint, "PUT", "/api/x", { token: TOKEN, body: { a: 1 } });
  assert.deepEqual(r, { status: 200, body: { ok: true } });
  assert.equal(got[0].auth, "Bearer " + TOKEN);
  assert.equal(got[0].host, `127.0.0.1:${endpoint.port}`);
  assert.equal(got[0].body, '{"a":1}');
  const moved = await agent.request(endpoint, "GET", "/moved", {});
  assert.equal(moved.status, 302);
  assert.equal(got.length, 2, "the redirect was not followed");
  assert.equal(got[1].auth, undefined, "no token without asking for it");
});

test("the start command is the agent's own background start", () => {
  assert.deepEqual(agent.startCommand("C:\\a\\construct-agent.exe", "win32"), { file: "C:\\a\\construct-agent.exe", args: ["--background"] });
  assert.deepEqual(agent.startCommand("/h/construct-agent", "linux", { exists: () => false }), { file: "/h/construct-agent", args: ["run"] });
  const unit = p => p === "/usr/bin/systemctl" || p === "/cfg/systemd/user/construct-agent.service";
  assert.deepEqual(agent.startCommand("/h/construct-agent", "linux", { exists: unit, configHome: "/cfg" }),
    { file: "/usr/bin/systemctl", args: ["--user", "start", "construct-agent.service"], unit: "/cfg/systemd/user/construct-agent.service" });
});

test("start runs the executable detached, and refuses one other users can write", t => {
  const home = tempHome(t);
  const exe = installVersion(home, "1.0.0");
  const calls = [];
  const spawn = (file, args, opts) => { calls.push({ file, args, opts }); return { on() {}, unref() { calls.at(-1).unref = true; } }; };
  agent.start({ exe, home }, { platform: "linux", spawn, exists: () => false });
  assert.equal(calls[0].file, exe);
  assert.deepEqual(calls[0].args, ["run"]);
  assert.equal(calls[0].opts.detached, true);
  assert.equal(calls[0].unref, true);
  fs.chmodSync(path.dirname(exe), 0o777);
  assert.throws(() => agent.start({ exe, home }, { platform: "linux", spawn }), /other users/);
  assert.equal(calls.length, 1);
});

test("start through the systemd unit refuses a unit other users can write", t => {
  const home = tempHome(t);
  const exe = installVersion(home, "1.0.0");
  const unitDir = path.join(home, "config", "systemd", "user");
  fs.mkdirSync(unitDir, { recursive: true, mode: 0o700 });
  fs.chmodSync(path.join(home, "config"), 0o700); fs.chmodSync(path.join(home, "config", "systemd"), 0o700);
  const unit = path.join(unitDir, "construct-agent.service");
  fs.writeFileSync(unit, "[Service]\n", { mode: 0o600 });
  const calls = [];
  const spawn = (file, args) => { calls.push(file); return { on() {}, unref() {} }; };
  const exists = p => p === "/usr/bin/systemctl" || fs.existsSync(p);
  const opts = { platform: "linux", spawn, exists, configHome: path.join(home, "config"), unitTop: home };
  agent.start({ exe, home }, opts);
  assert.deepEqual(calls, ["/usr/bin/systemctl"]);
  fs.chmodSync(unit, 0o666);
  assert.throws(() => agent.start({ exe, home }, opts), /systemd unit/);
  assert.equal(calls.length, 1);
});

test("privatePath walks from the file up to the top folder", t => {
  const home = tempHome(t);
  const exe = installVersion(home, "1.0.0");
  assert.ok(agent.privatePath(exe, home));
  assert.ok(!agent.privatePath(exe, home, { uid: process.getuid() + 1 }), "another owner");
  assert.ok(!agent.privatePath("/etc/passwd", home), "outside the top folder");
  fs.chmodSync(exe, 0o722);
  assert.ok(!agent.privatePath(exe, home), "group or world writable");
  assert.ok(agent.privatePath("C:\\x", "C:\\", { platform: "win32" }));
});

test("waitRunning polls until the agent answers", async () => {
  let n = 0;
  const found = await agent.waitRunning({ find: async () => (++n < 3 ? { state: "stopped" } : { state: "running", endpoint: { port: 1 } }), intervalMs: 1, timeoutMs: 1000 });
  assert.equal(found.state, "running");
  assert.equal(n, 3);
  assert.equal(await agent.waitRunning({ find: async () => ({ state: "missing" }), intervalMs: 1, timeoutMs: 5 }), null);
});

test("the install command is the agent's install script for the OS, by absolute paths", () => {
  const w = agent.installCommand("C:\\ext", "win32", { SystemRoot: "C:\\Windows" });
  assert.equal(w.file, "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe");
  assert.deepEqual(w.args, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "C:\\ext\\install\\Install-ConstructAgent.ps1"]);
  const l = agent.installCommand("/ext", "linux", {});
  assert.deepEqual(l, { script: "/ext/install/install-construct-agent.sh", file: "/bin/bash", args: ["/ext/install/install-construct-agent.sh"] });
  assert.equal(agent.installCommand("/ext", "darwin", {}), null);
});

test("install reports what failed, in place", async t => {
  const ext = tempHome(t);
  const none = await agent.install(ext, { platform: "linux" });
  assert.ok(!none.ok && /no install script/.test(none.error));
  fs.mkdirSync(path.join(ext, "install"), { mode: 0o700 });
  fs.writeFileSync(path.join(ext, "install", "install-construct-agent.sh"), "echo hi\n", { mode: 0o600 });
  const runs = [];
  const failing = (file, args, opts, cb) => { runs.push({ file, args }); cb(new Error("exit 1"), "downloading\n", "line1\nThe package does not match the SHA-256 in the channel manifest.\n"); };
  const failed = await agent.install(ext, { platform: "linux", execFile: failing });
  assert.deepEqual(runs[0], { file: "/bin/bash", args: [path.join(ext, "install", "install-construct-agent.sh")] });
  assert.ok(!failed.ok && /does not match the SHA-256/.test(failed.error));
  const ok = await agent.install(ext, { platform: "linux", execFile: (f, a, o, cb) => cb(null, "", "") });
  assert.deepEqual(ok, { ok: true });
  fs.chmodSync(path.join(ext, "install", "install-construct-agent.sh"), 0o666);
  const writable = await agent.install(ext, { platform: "linux", execFile: () => assert.fail("must not run") });
  assert.ok(!writable.ok && /other users/.test(writable.error));
  assert.ok(/Windows and Linux/.test((await agent.install(ext, { platform: "darwin" })).error));
});
